using Bazaar.Diagnostics.Grouping;
using Bazaar.Diagnostics.Ingestion;
using Bazaar.Diagnostics.Redaction;
using Microsoft.Extensions.Time.Testing;

namespace Bazaar.Server.Diagnostics.Tests;

public class CrashActionDispatcherTests
{
    private readonly InMemoryCrashStore _store = new();
    private readonly RecordingFiler _filer = new();
    private readonly RecordingAudit _audit = new();
    private readonly FakeTimeProvider _clock = new(new DateTimeOffset(2026, 11, 18, 10, 0, 0, TimeSpan.Zero));
    private readonly CrashFilingOptions _options = new() { IssueRepo = "codex/bazaar", MaxActionAttempts = 3 };

    private CrashIntakeService Intake(CrashFilingOptions? options = null) => new(
        new CrashIngestionPipeline(new AllowOwnership(), new Redactor(), new ResolvedSymbols()),
        _store,
        new FakeSealer(),
        _audit,
        _clock,
        options ?? _options);

    private CrashActionDispatcher Dispatcher(CrashFilingOptions? options = null) => new(_store, _store, _filer, _audit, _clock, options ?? _options);

    private async Task<string> FileFirstGroupAsync()
    {
        var accepted = Assert.IsType<CrashIntakeAccepted>(await Intake().ProcessAsync(new IngestionSubmission(TestReports.Report()), TestContext.Current.CancellationToken));
        await Dispatcher().RunOnceAsync(ct: TestContext.Current.CancellationToken);
        return accepted.GroupKey;
    }

    [Fact]
    public async Task File_row_creates_the_issue_with_marker_and_records_the_number()
    {
        var fingerprint = await FileFirstGroupAsync();

        var issue = Assert.Single(_filer.Created);
        Assert.StartsWith($"<!-- bazaar-crash-fp: {fingerprint} -->", issue.Body, StringComparison.Ordinal);
        Assert.Equal("BZ-NET-0001 System.InvalidOperationException", issue.Title);
        Assert.Equal(100, _store.Group(fingerprint)!.State.IssueNumber);
        Assert.Empty(_store.Outbox);
        Assert.Contains("crash.issue.created", _audit.Kinds);
    }

    [Fact]
    public async Task Running_twice_never_files_twice()
    {
        await FileFirstGroupAsync();

        await Dispatcher().RunOnceAsync(ct: TestContext.Current.CancellationToken);
        await Dispatcher().RunOnceAsync(ct: TestContext.Current.CancellationToken);

        Assert.Single(_filer.Created);
    }

    [Fact]
    public async Task Failure_reschedules_with_backoff_and_then_succeeds()
    {
        await Intake().ProcessAsync(new IngestionSubmission(TestReports.Report()), TestContext.Current.CancellationToken);
        _filer.FailWith = new HttpRequestException("down");

        var first = await Dispatcher().RunOnceAsync(ct: TestContext.Current.CancellationToken);
        var row = Assert.Single(_store.Outbox);

        Assert.Equal(1, first.Failed);
        Assert.Equal(1, row.Attempts);
        Assert.True(row.NotBefore > _clock.GetUtcNow());

        var tooSoon = await Dispatcher().RunOnceAsync(ct: TestContext.Current.CancellationToken);
        Assert.Equal(0, tooSoon.Completed + tooSoon.Failed);

        _filer.FailWith = null;
        _clock.Advance(TimeSpan.FromMinutes(2));
        var later = await Dispatcher().RunOnceAsync(ct: TestContext.Current.CancellationToken);
        Assert.Equal(1, later.Completed);
        Assert.Single(_filer.Created);
    }

    [Fact]
    public async Task Row_is_parked_after_too_many_failures_and_never_dropped()
    {
        await Intake().ProcessAsync(new IngestionSubmission(TestReports.Report()), TestContext.Current.CancellationToken);
        _filer.FailWith = new HttpRequestException("down");

        for (var i = 0; i < 3; i++)
        {
            await Dispatcher().RunOnceAsync(ct: TestContext.Current.CancellationToken);
            _clock.Advance(TimeSpan.FromHours(7));
        }

        Assert.Single(_store.Dead);
        Assert.Contains("crash.action.dead", _audit.Kinds);
        Assert.Single(_store.Outbox);
    }

    [Fact]
    public async Task Tenth_occurrence_comments_on_the_issue_once_a_day()
    {
        var fingerprint = await FileFirstGroupAsync();
        var group = _store.Group(fingerprint)!;
        _store.Seed(group with { State = group.State with { OccurrenceCount = 9 }, Version = group.Version });

        await Intake().ProcessAsync(new IngestionSubmission(TestReports.Report()), TestContext.Current.CancellationToken);
        await Dispatcher().RunOnceAsync(ct: TestContext.Current.CancellationToken);

        var comment = Assert.Single(_filer.Comments);
        Assert.Equal(100, comment.Issue);
        Assert.Contains("Seen 10 times", comment.Text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Comment_on_a_hand_deleted_issue_forgets_the_number_and_the_next_occurrence_files_again()
    {
        var fingerprint = await FileFirstGroupAsync();
        var group = _store.Group(fingerprint)!;
        _store.Seed(group with { State = group.State with { OccurrenceCount = 9 }, Version = group.Version });
        _filer.Deleted.Add(100);

        await Intake().ProcessAsync(new IngestionSubmission(TestReports.Report()), TestContext.Current.CancellationToken);
        await Dispatcher().RunOnceAsync(ct: TestContext.Current.CancellationToken);

        Assert.Null(_store.Group(fingerprint)!.State.IssueNumber);
        Assert.Contains("crash.issue.deleted_by_hand", _audit.Kinds);

        await Intake().ProcessAsync(new IngestionSubmission(TestReports.Report()), TestContext.Current.CancellationToken);
        await Dispatcher().RunOnceAsync(ct: TestContext.Current.CancellationToken);

        Assert.Equal(2, _filer.Created.Count);
        Assert.Equal(101, _store.Group(fingerprint)!.State.IssueNumber);
    }

    [Fact]
    public async Task Regression_from_the_fixed_version_reopens_the_issue()
    {
        var fingerprint = await FileFirstGroupAsync();
        var group = _store.Group(fingerprint)!;
        _store.Seed(group with { State = group.State.WithIssueClosed("0.2.0"), Version = group.Version });

        await Intake().ProcessAsync(new IngestionSubmission(TestReports.Report(version: "0.2.1+deadbee")), TestContext.Current.CancellationToken);
        await Dispatcher().RunOnceAsync(ct: TestContext.Current.CancellationToken);

        var reopened = Assert.Single(_filer.Reopened);
        Assert.Equal(100, reopened.Issue);
        Assert.Contains("0.2.0", reopened.Text, StringComparison.Ordinal);
        Assert.Equal(GroupStatus.ResolvedIn, _store.Group(fingerprint)!.State.Status);
        Assert.Contains("crash.issue.reopened", _audit.Kinds);
    }

    [Fact]
    public async Task Deferred_group_is_released_when_the_next_day_brings_budget()
    {
        var one = new CrashFilingOptions { IssueRepo = "codex/bazaar", IssuesPerDay = 1 };
        var intake = Intake(one);
        var first = Assert.IsType<CrashIntakeAccepted>(await intake.ProcessAsync(new IngestionSubmission(TestReports.Report(method: "A")), TestContext.Current.CancellationToken));
        var second = Assert.IsType<CrashIntakeAccepted>(await intake.ProcessAsync(new IngestionSubmission(TestReports.Report(method: "B")), TestContext.Current.CancellationToken));
        await Dispatcher(one).RunOnceAsync(ct: TestContext.Current.CancellationToken);
        Assert.Single(_filer.Created);

        var sameDay = await Dispatcher(one).RunOnceAsync(ct: TestContext.Current.CancellationToken);
        Assert.Equal(0, sameDay.Released);

        _clock.Advance(TimeSpan.FromDays(1));
        var nextDay = await Dispatcher(one).RunOnceAsync(ct: TestContext.Current.CancellationToken);

        Assert.Equal(1, nextDay.Released);
        Assert.Equal(2, _filer.Created.Count);
        Assert.NotNull(_store.Group(second.GroupKey)!.State.IssueNumber);
        Assert.NotNull(_store.Group(first.GroupKey)!.State.IssueNumber);
    }

    [Fact]
    public async Task Filing_off_does_nothing_and_loses_nothing()
    {
        await Intake(new CrashFilingOptions()).ProcessAsync(new IngestionSubmission(TestReports.Report()), TestContext.Current.CancellationToken);

        var summary = await Dispatcher(new CrashFilingOptions()).RunOnceAsync(ct: TestContext.Current.CancellationToken);

        Assert.Equal(new CrashDispatchSummary(0, 0, 0, 0), summary);
        Assert.Empty(_filer.Created);
    }
}
