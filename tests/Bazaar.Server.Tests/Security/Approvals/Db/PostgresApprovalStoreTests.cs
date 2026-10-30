using System.Collections.Immutable;
using Bazaar.Server.Security.Approvals;
using Bazaar.Server.Security.Approvals.Db;
using Bazaar.Server.Security.Taint;
using Bazaar.Testing.Postgres;
using Npgsql;

namespace Bazaar.Server.Tests.Security.Approvals.Db;

/// <summary>PostgresApprovalStore against a real PostgreSQL: insert, read back, compare-and-set, and the table CHECKs (doc 15).</summary>
public sealed class PostgresApprovalStoreTests
{
    private static void SkipWithoutDatabase() =>
        Assert.SkipWhen(PgSource.IsUnsupportedHost, "Windows without BAZAAR_TEST_PG: no test PostgreSQL available.");


    private static readonly DateTimeOffset _t0 = new(2026, 10, 26, 9, 0, 0, TimeSpan.Zero);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static byte[] Hash(byte seed) => Enumerable.Range(0, 32).Select(i => (byte)(seed + i)).ToArray();

    private static byte[] Signature(byte seed) => Enumerable.Range(0, 64).Select(i => (byte)(seed ^ i)).ToArray();

    private static ApprovalRequest NewPending(Guid id, string agentId = "agent:charles") => new(
        Id: id,
        AgentId: agentId,
        SessionId: Guid.Parse("11111111-2222-3333-4444-555555555555"),
        Tool: "github.merge",
        Target: null,
        ArgsHash: Hash(7),
        PayloadJson: "{\"path\": \"notes/a.md\"}",
        TaintSources: ImmutableArray<TaintSource>.Empty,
        Status: ApprovalStatus.Pending,
        RequestedAt: _t0,
        ExpiresAt: _t0.AddHours(24));

    private static ApprovalRequest Approved(ApprovalRequest pending, byte signatureSeed) => pending with
    {
        Status = ApprovalStatus.Approved,
        DecidedBy = "owner",
        DeviceId = "device-1",
        DecidedAt = _t0.AddMinutes(5),
        Signature = Signature(signatureSeed),
    };

    private static void AssertSameRow(ApprovalRequest expected, ApprovalRequest actual)
    {
        Assert.Equal(expected.Id, actual.Id);
        Assert.Equal(expected.AgentId, actual.AgentId);
        Assert.Equal(expected.SessionId, actual.SessionId);
        Assert.Equal(expected.Tool, actual.Tool);
        Assert.Equal(expected.Target, actual.Target);
        Assert.Equal(expected.ArgsHash, actual.ArgsHash);
        Assert.Equal(expected.PayloadJson, actual.PayloadJson);
        Assert.Equal(expected.TaintSources.Length, actual.TaintSources.Length);
        for (var i = 0; i < expected.TaintSources.Length; i++)
            Assert.Equal(expected.TaintSources[i], actual.TaintSources[i]);
        Assert.Equal(expected.Status, actual.Status);
        Assert.Equal(expected.RequestedAt, actual.RequestedAt);
        Assert.Equal(expected.ExpiresAt, actual.ExpiresAt);
        Assert.Equal(expected.DecidedBy, actual.DecidedBy);
        Assert.Equal(expected.DeviceId, actual.DeviceId);
        Assert.Equal(expected.DecidedAt, actual.DecidedAt);
        Assert.Equal(expected.Signature, actual.Signature);
        Assert.Equal(expected.ConsumedAt, actual.ConsumedAt);
    }

    [Fact]
    public async Task Insert_then_get_round_trips_every_field()
    {
        SkipWithoutDatabase();
        await using var db = await ApprovalTestDb.CreateAsync(Ct);
        var store = new PostgresApprovalStore(db.DataSource);

        var consumed = Approved(NewPending(Guid.NewGuid()) with
        {
            Target = "owner/repo#12",
            TaintSources =
            [
                new TaintSource(Guid.Parse("aaaaaaaa-0000-0000-0000-000000000001"), ContentTrust.External, TaintState.Hard),
                new TaintSource(Guid.Parse("aaaaaaaa-0000-0000-0000-000000000002"), ContentTrust.Agent, TaintState.Soft),
            ],
        }, signatureSeed: 3) with
        {
            Status = ApprovalStatus.Consumed,
            ConsumedAt = _t0.AddMinutes(6),
        };

        await store.InsertAsync(consumed, Ct);
        var read = await store.GetAsync(consumed.Id, Ct);

        Assert.NotNull(read);
        AssertSameRow(consumed, read);
        Assert.Equal(Hash(7), read.ArgsHash);
        Assert.Equal(2, read.TaintSources.Length);
        Assert.Equal(new TaintSource(Guid.Parse("aaaaaaaa-0000-0000-0000-000000000001"), ContentTrust.External, TaintState.Hard), read.TaintSources[0]);
    }

    [Fact]
    public async Task Get_returns_null_for_an_unknown_id()
    {
        SkipWithoutDatabase();
        await using var db = await ApprovalTestDb.CreateAsync(Ct);
        var store = new PostgresApprovalStore(db.DataSource);

        Assert.Null(await store.GetAsync(Guid.NewGuid(), Ct));
    }

    [Fact]
    public async Task Pending_row_reads_back_with_empty_taint_and_no_decision()
    {
        SkipWithoutDatabase();
        await using var db = await ApprovalTestDb.CreateAsync(Ct);
        var store = new PostgresApprovalStore(db.DataSource);
        var pending = NewPending(Guid.NewGuid());

        await store.InsertAsync(pending, Ct);
        var read = await store.GetAsync(pending.Id, Ct);

        Assert.NotNull(read);
        AssertSameRow(pending, read);
        Assert.True(read.TaintSources.IsEmpty);
        Assert.Null(read.Signature);
    }

    [Fact]
    public async Task TryReplace_returns_false_when_the_expected_status_is_stale()
    {
        SkipWithoutDatabase();
        await using var db = await ApprovalTestDb.CreateAsync(Ct);
        var store = new PostgresApprovalStore(db.DataSource);
        var pending = NewPending(Guid.NewGuid());
        await store.InsertAsync(pending, Ct);

        var approved = Approved(pending, signatureSeed: 1);
        Assert.True(await store.TryReplaceAsync(pending.Id, ApprovalStatus.Pending, approved, Ct));

        // The row is Approved now, so a caller that still expects Pending must be refused and leave the row alone.
        var denied = Approved(pending, signatureSeed: 2) with { Status = ApprovalStatus.Denied };
        Assert.False(await store.TryReplaceAsync(pending.Id, ApprovalStatus.Pending, denied, Ct));

        var read = await store.GetAsync(pending.Id, Ct);
        Assert.NotNull(read);
        Assert.Equal(ApprovalStatus.Approved, read.Status);
        Assert.Equal(approved.Signature, read.Signature);
    }

    [Fact]
    public async Task TryReplace_returns_false_for_an_unknown_id()
    {
        SkipWithoutDatabase();
        await using var db = await ApprovalTestDb.CreateAsync(Ct);
        var store = new PostgresApprovalStore(db.DataSource);
        var missing = NewPending(Guid.NewGuid());

        Assert.False(await store.TryReplaceAsync(missing.Id, ApprovalStatus.Pending, Approved(missing, 1), Ct));
        Assert.Null(await store.GetAsync(missing.Id, Ct));
    }

    [Fact]
    public async Task Concurrent_TryReplace_from_pending_has_exactly_one_winner()
    {
        SkipWithoutDatabase();
        await using var db = await ApprovalTestDb.CreateAsync(Ct);
        var store = new PostgresApprovalStore(db.DataSource);
        var pending = NewPending(Guid.NewGuid());
        await store.InsertAsync(pending, Ct);

        var attempts = Enumerable.Range(0, 10)
            .Select(i => store.TryReplaceAsync(pending.Id, ApprovalStatus.Pending, Approved(pending, (byte)(i + 1)), Ct))
            .ToArray();
        var results = await Task.WhenAll(attempts);

        Assert.Equal(1, results.Count(r => r));
        var read = await store.GetAsync(pending.Id, Ct);
        Assert.NotNull(read);
        Assert.Equal(ApprovalStatus.Approved, read.Status);
    }

    [Fact]
    public async Task Check_refuses_a_consumed_row_without_consumed_at()
    {
        SkipWithoutDatabase();
        await using var db = await ApprovalTestDb.CreateAsync(Ct);
        var store = new PostgresApprovalStore(db.DataSource);
        var pending = NewPending(Guid.NewGuid());
        await store.InsertAsync(pending, Ct);

        var consumedWithoutTime = Approved(pending, signatureSeed: 4) with { Status = ApprovalStatus.Consumed };
        var ex = await Assert.ThrowsAsync<PostgresException>(() =>
            store.TryReplaceAsync(pending.Id, ApprovalStatus.Pending, consumedWithoutTime, Ct));
        Assert.Equal("23514", ex.SqlState); // check_violation

        Assert.Equal(ApprovalStatus.Pending, (await store.GetAsync(pending.Id, Ct))!.Status);
    }

    [Fact]
    public async Task Check_refuses_an_approved_row_without_a_signature()
    {
        SkipWithoutDatabase();
        await using var db = await ApprovalTestDb.CreateAsync(Ct);
        var store = new PostgresApprovalStore(db.DataSource);
        var pending = NewPending(Guid.NewGuid());
        await store.InsertAsync(pending, Ct);

        var unsigned = Approved(pending, signatureSeed: 5) with { Signature = null };
        var ex = await Assert.ThrowsAsync<PostgresException>(() =>
            store.TryReplaceAsync(pending.Id, ApprovalStatus.Pending, unsigned, Ct));
        Assert.Equal("23514", ex.SqlState);
    }

    [Fact]
    public async Task Check_refuses_an_args_hash_that_is_not_32_bytes()
    {
        SkipWithoutDatabase();
        await using var db = await ApprovalTestDb.CreateAsync(Ct);
        var store = new PostgresApprovalStore(db.DataSource);
        var bad = NewPending(Guid.NewGuid()) with { ArgsHash = new byte[31] };

        var ex = await Assert.ThrowsAsync<PostgresException>(() => store.InsertAsync(bad, Ct));
        Assert.Equal("23514", ex.SqlState);
    }
}
