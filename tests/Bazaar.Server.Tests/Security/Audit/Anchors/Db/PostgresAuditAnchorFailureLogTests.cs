using Bazaar.Server.Security.Audit;
using Bazaar.Server.Security.Audit.Anchors;
using Bazaar.Server.Security.Audit.Db;
using Bazaar.Server.Tests.Security.Audit.Db;
using Bazaar.Testing.Postgres;

namespace Bazaar.Server.Tests.Security.Audit.Anchors.Db;

/// <summary>PostgresAuditAnchorFailureLog against a real PostgreSQL: a failure is found by checkpoint and target only (design doc 48, 5.3).</summary>
public sealed class PostgresAuditAnchorFailureLogTests
{
    private static readonly DateTimeOffset _t0 = new(2026, 11, 15, 12, 0, 0, TimeSpan.Zero);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static void SkipWithoutDatabase() =>
        Assert.SkipWhen(PgSource.IsUnsupportedHost, "Windows without BAZAAR_TEST_PG: no test PostgreSQL available.");

    [Fact]
    public async Task A_recorded_failure_is_found_for_its_checkpoint_and_target_only()
    {
        SkipWithoutDatabase();
        await using var db = await AuditTestDb.CreateAsync(Ct);
        var log = new PostgresAuditLog(db.DataSource);
        for (var i = 0; i < 5; i++)
            await log.AppendAsync(new AuditEntry(_t0.AddSeconds(i), "agent:charles", "tool.call", $"{{\"n\": {i}}}"), Ct);
        await log.AppendAsync(new AuditEntry(_t0.AddSeconds(6), "system", AuditAnchorService.FailedKind, "{\"last_seq\": 5, \"target_id\": \"rfc3161\", \"reason\": \"timeout\"}"), Ct);
        var reader = new PostgresAuditAnchorFailureLog(db.DataSource);

        Assert.True(await reader.HasRecordedFailureAsync(5, "rfc3161", Ct));
        Assert.False(await reader.HasRecordedFailureAsync(5, "offsite-git", Ct));
        Assert.False(await reader.HasRecordedFailureAsync(4, "rfc3161", Ct));
    }

    [Fact]
    public async Task An_empty_log_has_no_recorded_failure()
    {
        SkipWithoutDatabase();
        await using var db = await AuditTestDb.CreateAsync(Ct);

        Assert.False(await new PostgresAuditAnchorFailureLog(db.DataSource).HasRecordedFailureAsync(5, "rfc3161", Ct));
    }
}
