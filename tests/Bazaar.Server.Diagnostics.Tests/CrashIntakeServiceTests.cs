using System.Security.Cryptography;
using System.Text;
using Bazaar.Diagnostics.Grouping;
using Bazaar.Diagnostics.Ingestion;
using Bazaar.Diagnostics.Redaction;
using Microsoft.Extensions.Time.Testing;

namespace Bazaar.Server.Diagnostics.Tests;

public class CrashIntakeServiceTests
{
    private const string _canary = "canary-7f3a9c41";

    private readonly InMemoryCrashStore _store = new();
    private readonly FakeSealer _sealer = new();
    private readonly RecordingAudit _audit = new();
    private readonly FakeTimeProvider _clock = new(new DateTimeOffset(2026, 11, 18, 10, 0, 0, TimeSpan.Zero));

    private CrashIntakeService Service(CrashFilingOptions? options = null) => new(
        new CrashIngestionPipeline(new AllowOwnership(), new Redactor([_canary]), new ResolvedSymbols()),
        _store,
        _sealer,
        _audit,
        _clock,
        options ?? new CrashFilingOptions { IssueRepo = "codex/bazaar" });

    private static IngestionSubmission Submit(Bazaar.Contracts.Telemetry.CrashReport report, IngestionAttachment? attachment = null) => new(report, attachment);

    [Fact]
    public async Task Accepted_report_is_stored_sealed_and_files_one_issue()
    {
        var report = TestReports.Report();

        var result = await Service().ProcessAsync(Submit(report), TestContext.Current.CancellationToken);

        var accepted = Assert.IsType<CrashIntakeAccepted>(result);
        Assert.False(accepted.Duplicate);
        var stored = Assert.Single(_store.Reports);
        Assert.Equal(accepted.GroupKey, stored.Fingerprint);
        Assert.DoesNotContain("connection refused", Encoding.UTF8.GetString(stored.Body.Ciphertext), StringComparison.Ordinal);
        var plain = await _sealer.OpenAsync(report.InstallId, SealedPayloadKind.Report, report.ReportId, stored.Body, TestContext.Current.CancellationToken);
        Assert.Contains("connection refused", Encoding.UTF8.GetString(plain), StringComparison.Ordinal);
        Assert.Equal(Convert.ToHexStringLower(SHA256.HashData(plain)), stored.BodySha256);
        var row = Assert.Single(_store.Outbox);
        Assert.IsType<FileIssue>(row.Action);
        Assert.Equal(GroupStatus.Open, _store.Group(accepted.GroupKey)!.State.Status);
    }

    [Fact]
    public async Task Second_report_in_the_same_group_does_not_queue_a_second_filing()
    {
        var service = Service();
        await service.ProcessAsync(Submit(TestReports.Report()), TestContext.Current.CancellationToken);

        var second = Assert.IsType<CrashIntakeAccepted>(await service.ProcessAsync(Submit(TestReports.Report()), TestContext.Current.CancellationToken));

        Assert.Equal(2, _store.Group(second.GroupKey)!.State.OccurrenceCount);
        Assert.Single(_store.Outbox);
        Assert.Equal(2, _store.Reports.Count);
    }

    [Fact]
    public async Task Retried_submission_is_acknowledged_without_a_second_write()
    {
        var report = TestReports.Report();
        var service = Service();
        await service.ProcessAsync(Submit(report), TestContext.Current.CancellationToken);

        var again = Assert.IsType<CrashIntakeAccepted>(await service.ProcessAsync(Submit(report), TestContext.Current.CancellationToken));

        Assert.True(again.Duplicate);
        Assert.Single(_store.Reports);
        Assert.Equal(1, _store.Group(again.GroupKey)!.State.OccurrenceCount);
        Assert.Contains("crash.report.duplicate", _audit.Kinds);
    }

    [Fact]
    public async Task Refused_report_stores_nothing_and_audits_the_code_without_content()
    {
        var report = TestReports.Report(message: $"token {_canary}");

        var result = await Service().ProcessAsync(Submit(report), TestContext.Current.CancellationToken);

        var refused = Assert.IsType<CrashIntakeRefused>(result);
        Assert.Equal(IngestionRefusalCode.RedactionResidual, refused.Code);
        Assert.Empty(_store.Reports);
        var entry = Assert.Single(_audit.Entries);
        Assert.Equal("crash.report.refused", entry.Kind);
        Assert.DoesNotContain(_canary, entry.Payload, StringComparison.Ordinal);
        Assert.Contains("redaction_residual", entry.Payload, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Empty_issue_repo_keeps_filing_off_and_defers_the_group()
    {
        var result = Assert.IsType<CrashIntakeAccepted>(await Service(new CrashFilingOptions()).ProcessAsync(Submit(TestReports.Report()), TestContext.Current.CancellationToken));

        Assert.Empty(_store.Outbox);
        Assert.Equal(GroupStatus.FilingDeferred, _store.Group(result.GroupKey)!.State.Status);
    }

    [Fact]
    public async Task Daily_budget_defers_the_second_new_group()
    {
        var service = Service(new CrashFilingOptions { IssueRepo = "codex/bazaar", IssuesPerDay = 1 });

        var first = Assert.IsType<CrashIntakeAccepted>(await service.ProcessAsync(Submit(TestReports.Report(method: "A")), TestContext.Current.CancellationToken));
        var second = Assert.IsType<CrashIntakeAccepted>(await service.ProcessAsync(Submit(TestReports.Report(method: "B")), TestContext.Current.CancellationToken));

        Assert.Equal(GroupStatus.Open, _store.Group(first.GroupKey)!.State.Status);
        Assert.Equal(GroupStatus.FilingDeferred, _store.Group(second.GroupKey)!.State.Status);
        Assert.Single(_store.Outbox);
    }

    [Fact]
    public async Task Version_conflict_recomputes_and_still_commits()
    {
        _store.ForcedConflicts = 2;

        var result = await Service().ProcessAsync(Submit(TestReports.Report()), TestContext.Current.CancellationToken);

        Assert.IsType<CrashIntakeAccepted>(result);
        Assert.Single(_store.Reports);
    }

    [Fact]
    public async Task Endless_conflicts_throw_and_store_nothing()
    {
        _store.ForcedConflicts = int.MaxValue;

        await Assert.ThrowsAsync<CrashIntakeContentionException>(() => Service().ProcessAsync(Submit(TestReports.Report()), TestContext.Current.CancellationToken));

        Assert.Empty(_store.Reports);
    }

    [Fact]
    public async Task Destroying_the_install_key_makes_the_stored_report_unreadable()
    {
        var report = TestReports.Report();
        await Service().ProcessAsync(Submit(report), TestContext.Current.CancellationToken);
        var stored = Assert.Single(_store.Reports);

        _sealer.DestroyKey(report.InstallId);

        await Assert.ThrowsAsync<CryptographicException>(() => _sealer.OpenAsync(report.InstallId, SealedPayloadKind.Report, report.ReportId, stored.Body, TestContext.Current.CancellationToken));
        Assert.NotNull(_store.Group(stored.Fingerprint));
    }

    [Fact]
    public async Task Sealed_body_cannot_be_moved_to_another_report_id()
    {
        var report = TestReports.Report();
        await Service().ProcessAsync(Submit(report), TestContext.Current.CancellationToken);
        var stored = Assert.Single(_store.Reports);

        await Assert.ThrowsAnyAsync<CryptographicException>(() => _sealer.OpenAsync(report.InstallId, SealedPayloadKind.Report, Guid.NewGuid(), stored.Body, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Audit_entries_carry_ids_and_hashes_never_content()
    {
        await Service().ProcessAsync(Submit(TestReports.Report(message: "connection refused to the secret host")), TestContext.Current.CancellationToken);

        Assert.Contains("crash.report.accepted", _audit.Kinds);
        Assert.Contains("crash.group.status", _audit.Kinds);
        Assert.All(_audit.Entries, entry =>
        {
            Assert.DoesNotContain("connection refused", entry.Payload, StringComparison.Ordinal);
            Assert.DoesNotContain("secret host", entry.Payload, StringComparison.Ordinal);
        });
    }

    [Fact]
    public async Task Mismatched_device_hint_is_audited_and_the_server_value_is_the_key()
    {
        var report = TestReports.Report() with { Fingerprint = "fp1:00000000000000000000000000000000" };

        var result = Assert.IsType<CrashIntakeAccepted>(await Service().ProcessAsync(Submit(report), TestContext.Current.CancellationToken));

        Assert.NotEqual("fp1:00000000000000000000000000000000", result.GroupKey);
        Assert.Contains("crash.fingerprint.mismatch", _audit.Kinds);
    }
}
