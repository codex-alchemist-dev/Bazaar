using Bazaar.Server.Security.Audit;
using Bazaar.Server.Security.Audit.Db;
using Bazaar.Testing.Postgres;
using Npgsql;

namespace Bazaar.Server.Tests.Security.Audit.Db;

/// <summary>
/// The verifier catches changes made by a superuser who turns the trigger off, which is the case the trigger cannot stop.
/// It shows that the chain is detected on DB rows, and that re-chaining is caught only by an external anchor (the head hash
/// of a signed export, doc 15).
/// </summary>
public sealed class AuditTamperTests
{
    private static readonly DateTimeOffset _t0 = new(2026, 10, 25, 12, 0, 0, TimeSpan.Zero);
    private const string _trigger = "audit_log_no_update_or_delete";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static void SkipWithoutDatabase() =>
        Assert.SkipWhen(PgSource.IsUnsupportedHost, "Windows without BAZAAR_TEST_PG: no test PostgreSQL available.");

    private static async Task ExecAsync(NpgsqlDataSource ds, string sql, params NpgsqlParameter[] parameters)
    {
        await using var cmd = ds.CreateCommand(sql);
        cmd.Parameters.AddRange(parameters);
        await cmd.ExecuteNonQueryAsync(Ct);
    }

    /// <summary>Runs <paramref name="body"/> with the update/delete trigger switched off, as a superuser would; switches it back on afterwards.</summary>
    private static async Task WithTriggerOffAsync(NpgsqlDataSource ds, Func<Task> body)
    {
        await ExecAsync(ds, $"ALTER TABLE audit_log DISABLE TRIGGER {_trigger}");
        try
        {
            await body();
        }
        finally
        {
            await ExecAsync(ds, $"ALTER TABLE audit_log ENABLE TRIGGER {_trigger}");
        }
    }

    private static async Task SeedAsync(NpgsqlDataSource ds, int count)
    {
        var log = new PostgresAuditLog(ds);
        for (var i = 1; i <= count; i++)
            await log.AppendAsync(new AuditEntry(_t0.AddSeconds(i), "owner", "config.change", $"{{\"step\": {i}}}"), Ct);
    }

    [Fact]
    public async Task Edited_row_breaks_verification_at_that_row()
    {
        SkipWithoutDatabase();
        await using var db = await AuditTestDb.CreateAsync(Ct);
        await SeedAsync(db.DataSource, 3);
        var log = new PostgresAuditLog(db.DataSource);

        await WithTriggerOffAsync(db.DataSource, () => ExecAsync(db.DataSource,
            "UPDATE audit_log SET payload = CAST($1 AS jsonb) WHERE seq = 2",
            new NpgsqlParameter { Value = "{\"step\": 99}" }));

        var verification = await log.VerifyAsync(ct: Ct);
        Assert.False(verification.IsValid);
        Assert.Equal(2, verification.FailedSeq);
        Assert.Equal(AuditFailure.HashMismatch, verification.Failure);

        // The trigger is back on: the same edit is refused now.
        await using var cmd = db.DataSource.CreateCommand("UPDATE audit_log SET payload = '{}' WHERE seq = 2");
        var ex = await Assert.ThrowsAsync<PostgresException>(() => cmd.ExecuteNonQueryAsync(Ct));
        Assert.Equal("42501", ex.SqlState);
    }

    [Fact]
    public async Task Deleted_middle_row_breaks_the_sequence()
    {
        SkipWithoutDatabase();
        await using var db = await AuditTestDb.CreateAsync(Ct);
        await SeedAsync(db.DataSource, 3);

        await WithTriggerOffAsync(db.DataSource, () => ExecAsync(db.DataSource, "DELETE FROM audit_log WHERE seq = 2"));

        var verification = await new PostgresAuditLog(db.DataSource).VerifyAsync(ct: Ct);
        Assert.False(verification.IsValid);
        Assert.Equal(AuditFailure.SequenceBroken, verification.Failure);
        Assert.Equal(3, verification.FailedSeq);
    }

    [Fact]
    public async Task Deleted_tail_passes_the_links_but_not_against_the_known_head()
    {
        SkipWithoutDatabase();
        await using var db = await AuditTestDb.CreateAsync(Ct);
        await SeedAsync(db.DataSource, 3);
        var log = new PostgresAuditLog(db.DataSource);
        var head = await log.ReadHeadAsync(Ct);

        await WithTriggerOffAsync(db.DataSource, () => ExecAsync(db.DataSource, "DELETE FROM audit_log WHERE seq = 3"));

        // A cut-off tail is a valid shorter chain; only the head anchored outside the database exposes it.
        var plain = await log.VerifyAsync(ct: Ct);
        Assert.True(plain.IsValid);
        Assert.Equal(2, plain.Checked);

        var anchored = await log.VerifyAsync(head!.Hash, Ct);
        Assert.False(anchored.IsValid);
        Assert.Equal(AuditFailure.HeadMismatch, anchored.Failure);
    }

    [Fact]
    public async Task Rechained_tail_passes_the_links_and_is_caught_only_by_the_anchored_head()
    {
        SkipWithoutDatabase();
        await using var db = await AuditTestDb.CreateAsync(Ct);
        await SeedAsync(db.DataSource, 3);
        var log = new PostgresAuditLog(db.DataSource);
        var originalHead = (await log.ReadHeadAsync(Ct))!;

        // A superuser rewrites seq 2 and 3 and recomputes their hashes, so the chain is self-consistent again.
        var rows = new List<AuditRecord>();
        await foreach (var record in log.StreamAsync(Ct))
            rows.Add(record);
        var rewritten2 = AuditChain.Append(rows[0], rows[1].Ts, rows[1].Actor, rows[1].Kind, "{\"step\": 99}");
        var rewritten3 = AuditChain.Append(rewritten2, rows[2].Ts, rows[2].Actor, rows[2].Kind, rows[2].Payload);

        await WithTriggerOffAsync(db.DataSource, async () =>
        {
            foreach (var row in new[] { rewritten2, rewritten3 })
            {
                await ExecAsync(db.DataSource,
                    "UPDATE audit_log SET payload = CAST($2 AS jsonb), prev_hash = $3, hash = $4 WHERE seq = $1",
                    new NpgsqlParameter { Value = row.Seq },
                    new NpgsqlParameter { Value = row.Payload },
                    new NpgsqlParameter { Value = row.PrevHash },
                    new NpgsqlParameter { Value = row.Hash });
            }
        });

        Assert.True((await log.VerifyAsync(ct: Ct)).IsValid);
        var anchored = await log.VerifyAsync(originalHead.Hash, Ct);
        Assert.False(anchored.IsValid);
        Assert.Equal(AuditFailure.HeadMismatch, anchored.Failure);
        Assert.True((await log.VerifyAsync(rewritten3.Hash, Ct)).IsValid);
    }
}
