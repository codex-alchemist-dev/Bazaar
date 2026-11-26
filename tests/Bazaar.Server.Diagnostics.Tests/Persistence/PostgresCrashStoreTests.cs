using Bazaar.Server.Data.Migrations;
using Bazaar.Server.Diagnostics.Persistence;
using Bazaar.Testing.Postgres;

namespace Bazaar.Server.Diagnostics.Tests.Persistence;

public sealed class PostgresCrashStoreTests : CrashStoreContract
{
    protected override async Task<(ICrashIntakeStore Store, ICrashActionOutbox Outbox, IAsyncDisposable Handle)> CreateAsync(CancellationToken ct)
    {
        Assert.SkipWhen(PgSource.IsUnsupportedHost, "Windows without BAZAAR_TEST_PG: no test PostgreSQL available.");
        var db = await PgTestDatabase.CreateAsync(
            "crash",
            async (ds, token) =>
            {
                await PgRoles.EnsureNoLoginRoleAsync(ds, "bazaar_app", token).ConfigureAwait(false);
                await new MigrationRunner(ds).MigrateAsync(
                    new EmbeddedMigrationSource(typeof(PostgresCrashStore).Assembly), MigrateCommand.CurrentProvider(), token).ConfigureAwait(false);
            },
            ct);
        var store = new PostgresCrashStore(db.DataSource);
        return (store, store, db);
    }

    [Fact]
    public async Task Retention_removes_screenshots_then_reports_then_idle_groups_and_keeps_what_is_still_needed()
    {
        var ct = TestContext.Current.CancellationToken;
        var (store, _, handle) = await CreateAsync(ct);
        await using var _h = handle;
        var retention = (ICrashRetention)store;
        var t0 = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        const string fp = "fp1:0123456789abcdef0123456789abcdef";
        var group = new StoredCrashGroup(
            new Bazaar.Diagnostics.Grouping.GroupState { Fingerprint = fp, OccurrenceCount = 1, FirstSeenVersion = "0.1.0", LastSeenVersion = "0.1.0", FirstSeenAt = t0, LastSeenAt = t0 },
            new CrashIssueFacts(null, "System.Exception", [], null, null), 1);
        var report = new StoredCrashReport(Guid.NewGuid(), Guid.NewGuid(), fp, t0, t0, "0.1.0", Bazaar.Diagnostics.Ingestion.SymbolsState.Missing, new string('a', 64), new SealedPayload("k", [1], [2], [3]));
        var shot = new StoredCrashAttachment(Guid.NewGuid(), report.ReportId, "image/png", new string('b', 64), new SealedPayload("k", [1], [2], [3]));
        await store.CommitAsync(new CrashCommit(report, shot, group, null, []), ct);
        var policy = CrashRetentionPolicy.Default;

        var early = await retention.PurgeAsync(t0.AddDays(29), policy, ct);
        var afterShot = await retention.PurgeAsync(t0.AddDays(31), policy, ct);
        var afterReport = await retention.PurgeAsync(t0.AddDays(91), policy, ct);
        var groupStillThere = await store.GetGroupAsync(fp, ct);
        var afterGroup = await retention.PurgeAsync(t0.AddDays(366), policy, ct);

        Assert.Equal(new CrashPurgeResult(0, 0, 0), early);
        Assert.Equal(new CrashPurgeResult(1, 0, 0), afterShot);
        Assert.Equal(new CrashPurgeResult(0, 1, 0), afterReport);
        Assert.NotNull(groupStillThere);
        Assert.Equal(new CrashPurgeResult(0, 0, 1), afterGroup);
        Assert.Null(await store.GetGroupAsync(fp, ct));
    }

    [Fact]
    public async Task Erasing_installs_removes_their_reports_and_screenshots_and_keeps_the_group()
    {
        var ct = TestContext.Current.CancellationToken;
        var (store, _, handle) = await CreateAsync(ct);
        await using var _h = handle;
        var t0 = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        const string fp = "fp1:0123456789abcdef0123456789abcdef";
        var group = new StoredCrashGroup(
            new Bazaar.Diagnostics.Grouping.GroupState { Fingerprint = fp, OccurrenceCount = 2, FirstSeenVersion = "0.1.0", LastSeenVersion = "0.1.0", FirstSeenAt = t0, LastSeenAt = t0 },
            new CrashIssueFacts(null, "System.Exception", [], null, null), 1);
        var sealedBody = new SealedPayload("k", [1], [2], [3]);
        var mine = Guid.NewGuid();
        var other = Guid.NewGuid();
        var report = new StoredCrashReport(Guid.NewGuid(), mine, fp, t0, t0, "0.1.0", Bazaar.Diagnostics.Ingestion.SymbolsState.Missing, new string('a', 64), sealedBody);
        var shot = new StoredCrashAttachment(Guid.NewGuid(), report.ReportId, "image/png", new string('b', 64), sealedBody);
        await store.CommitAsync(new CrashCommit(report, shot, group, null, []), ct);
        var kept = new StoredCrashReport(Guid.NewGuid(), other, fp, t0, t0, "0.1.0", Bazaar.Diagnostics.Ingestion.SymbolsState.Missing, new string('c', 64), sealedBody);
        await store.CommitAsync(new CrashCommit(kept, null, group, 1, []), ct);
        var erasure = (ICrashErasure)store;

        var first = await erasure.EraseInstallsAsync([mine], ct);
        var again = await erasure.EraseInstallsAsync([mine], ct);
        var none = await erasure.EraseInstallsAsync([], ct);

        Assert.Equal(new CrashEraseResult(1, 1), first);
        Assert.Equal(new CrashEraseResult(0, 0), again);
        Assert.Equal(new CrashEraseResult(0, 0), none);
        Assert.NotNull(await store.GetGroupAsync(fp, ct));
        Assert.Equal(new CrashEraseResult(1, 0), await erasure.EraseInstallsAsync([other], ct));
    }

    [Fact]
    public async Task Diagnoses_round_trip_the_database_refuses_another_label_and_erasing_the_report_removes_its_diagnosis()
    {
        var ct = TestContext.Current.CancellationToken;
        var (store, _, handle) = await CreateAsync(ct);
        await using var _h = handle;
        var t0 = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        const string fp = "fp1:0123456789abcdef0123456789abcdef";
        var group = new StoredCrashGroup(
            new Bazaar.Diagnostics.Grouping.GroupState { Fingerprint = fp, OccurrenceCount = 1, FirstSeenVersion = "0.1.0", LastSeenVersion = "0.1.0", FirstSeenAt = t0, LastSeenAt = t0 },
            new CrashIssueFacts(null, "System.Exception", [], null, null), 1);
        var install = Guid.NewGuid();
        var report = new StoredCrashReport(Guid.NewGuid(), install, fp, t0, t0, "0.1.0", Bazaar.Diagnostics.Ingestion.SymbolsState.Missing, new string('a', 64), new SealedPayload("k", [1], [2], [3]));
        await store.CommitAsync(new CrashCommit(report, null, group, null, []), ct);
        var diagnoses = (ICrashDiagnosisStore)store;
        var made = new StoredCrashDiagnosis(Guid.NewGuid(), fp, report.ReportId, CrashDiagnosisLabel.Unverified, t0, "light-model", new byte[32], "Check it.", false, 1);

        await diagnoses.AddAsync(made, ct);
        await diagnoses.MarkPostedAsync(made.DiagnosisId, ct);
        var read = await diagnoses.GetLatestAsync(fp, ct);
        var latest = await ((ICrashReportReader)store).GetLatestReportAsync(fp, ct);

        Assert.True(read!.PostedToIssue);
        Assert.Equal("Check it.", read.Text);
        Assert.Equal(report.ReportId, latest!.ReportId);
        Assert.Equal(report.Body.Ciphertext, latest.Body.Ciphertext);
        await Assert.ThrowsAnyAsync<Exception>(() => diagnoses.AddAsync(made with { DiagnosisId = Guid.NewGuid(), Label = "verified" }, ct));

        Assert.Empty(await diagnoses.ListNeedingAutoDiagnosisAsync(t0, 10, ct));
        var bumped = group with { State = group.State with { OccurrenceCount = 10 }, Version = 2 };
        await store.CommitAsync(new CrashCommit(null, null, bumped, 1, []), ct);
        Assert.Equal([fp], await diagnoses.ListNeedingAutoDiagnosisAsync(t0, 10, ct));

        await diagnoses.RecordAutoFailureAsync(fp, t0, ct);
        Assert.Empty(await diagnoses.ListNeedingAutoDiagnosisAsync(t0.AddMinutes(9), 10, ct));
        Assert.Equal([fp], await diagnoses.ListNeedingAutoDiagnosisAsync(t0.AddMinutes(11), 10, ct));
        await diagnoses.RecordAutoFailureAsync(fp, t0, ct);
        Assert.Empty(await diagnoses.ListNeedingAutoDiagnosisAsync(t0.AddMinutes(11), 10, ct));
        await diagnoses.AddAsync(made with { DiagnosisId = Guid.NewGuid(), ProducedAt = t0.AddMinutes(1), OccurrenceCount = 10 }, ct);
        Assert.Empty(await diagnoses.ListNeedingAutoDiagnosisAsync(t0.AddMinutes(11), 10, ct));

        await ((ICrashErasure)store).EraseInstallsAsync([install], ct);

        Assert.Null(await diagnoses.GetLatestAsync(fp, ct));
    }
}
