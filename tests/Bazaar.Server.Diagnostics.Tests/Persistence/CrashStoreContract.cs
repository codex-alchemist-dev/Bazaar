using System.Collections.Immutable;
using Bazaar.Diagnostics.Fingerprinting;
using Bazaar.Diagnostics.Grouping;
using Bazaar.Diagnostics.Ingestion;

namespace Bazaar.Server.Diagnostics.Tests.Persistence;

/// <summary>The behaviour every crash store must have. The in-memory fake and the Postgres store both run it, so the fake cannot drift.</summary>
public abstract class CrashStoreContract
{
    private const string _fp = "fp1:0123456789abcdef0123456789abcdef";
    private static readonly DateTimeOffset _t0 = new(2026, 11, 18, 10, 0, 0, TimeSpan.Zero);

    /// <summary>Creates a fresh, empty store. The returned handle disposes whatever backs it.</summary>
    protected abstract Task<(ICrashIntakeStore Store, ICrashActionOutbox Outbox, IAsyncDisposable Handle)> CreateAsync(CancellationToken ct);

    private static StoredCrashGroup NewGroup(string fp = _fp, GroupStatus status = GroupStatus.Open) => new(
        new GroupState
        {
            Fingerprint = fp,
            OccurrenceCount = 1,
            FirstSeenVersion = "0.1.0",
            LastSeenVersion = "0.1.0",
            FirstSeenAt = _t0,
            LastSeenAt = _t0,
            DeviceClasses = ["desktop"],
            OsFamilies = ["windows"],
            Status = status,
        },
        new CrashIssueFacts("BZ-NET-0001", "System.InvalidOperationException", [new FingerprintFrame("Bazaar.Client.Core", "Session", "Connect", true)], 42, "connection refused"),
        1);

    private static StoredCrashReport Report(Guid? id = null, string fp = _fp) => new(
        id ?? Guid.NewGuid(), Guid.NewGuid(), fp, _t0, _t0.AddSeconds(1), "0.1.0", SymbolsState.Resolved,
        new string('a', 64), new SealedPayload("key-1", [1, 2, 3], [4, 5, 6, 7], [8, 9]));

    private static PendingCrashAction Action(GroupAction action, DateTimeOffset? at = null) =>
        new(Guid.NewGuid(), _fp, action, at ?? _t0, 0, at ?? _t0);

    [Fact]
    public async Task A_new_group_commits_with_its_report_and_reads_back_unchanged()
    {
        var ct = TestContext.Current.CancellationToken;
        var (store, _, handle) = await CreateAsync(ct);
        await using var _h = handle;
        var group = NewGroup();

        var status = await store.CommitAsync(new CrashCommit(Report(), null, group, null, []), ct);
        var read = await store.GetGroupAsync(_fp, ct);

        Assert.Equal(CrashCommitStatus.Committed, status);
        Assert.NotNull(read);
        Assert.Equal(1, read.Version);
        Assert.Equal(group.State with { DeviceClasses = [], OsFamilies = [] }, read.State with { DeviceClasses = [], OsFamilies = [] });
        Assert.Equal(group.State.DeviceClasses, read.State.DeviceClasses);
        Assert.Equal(group.State.OsFamilies, read.State.OsFamilies);
        Assert.Equal("BZ-NET-0001", read.Facts.ErrorCode);
        Assert.Equal(42, read.Facts.FlaggedLine);
        Assert.Equal("Connect", read.Facts.Frames[0].Method);
    }

    [Fact]
    public async Task An_unknown_group_reads_as_null()
    {
        var ct = TestContext.Current.CancellationToken;
        var (store, _, handle) = await CreateAsync(ct);
        await using var _h = handle;

        Assert.Null(await store.GetGroupAsync(_fp, ct));
    }

    [Fact]
    public async Task Creating_a_group_that_already_exists_is_a_version_conflict_and_writes_nothing()
    {
        var ct = TestContext.Current.CancellationToken;
        var (store, _, handle) = await CreateAsync(ct);
        await using var _h = handle;
        await store.CommitAsync(new CrashCommit(Report(), null, NewGroup(), null, []), ct);

        var status = await store.CommitAsync(new CrashCommit(Report(), null, NewGroup(), null, [Action(new FileIssue(_fp))]), ct);

        Assert.Equal(CrashCommitStatus.VersionConflict, status);
        Assert.Equal(0, await store.CountIssueFilingsAsync(DateOnly.FromDateTime(_t0.UtcDateTime), ct));
    }

    [Fact]
    public async Task An_update_with_the_current_version_bumps_it_and_a_stale_version_is_refused()
    {
        var ct = TestContext.Current.CancellationToken;
        var (store, _, handle) = await CreateAsync(ct);
        await using var _h = handle;
        await store.CommitAsync(new CrashCommit(Report(), null, NewGroup(), null, []), ct);
        var next = NewGroup();
        next = next with { State = next.State with { OccurrenceCount = 2, LastSeenVersion = "0.1.1" } };

        var ok = await store.CommitAsync(new CrashCommit(Report(), null, next, 1, []), ct);
        var stale = await store.CommitAsync(new CrashCommit(Report(), null, next with { State = next.State with { OccurrenceCount = 9 } }, 1, []), ct);
        var read = await store.GetGroupAsync(_fp, ct);

        Assert.Equal(CrashCommitStatus.Committed, ok);
        Assert.Equal(CrashCommitStatus.VersionConflict, stale);
        Assert.Equal(2, read!.Version);
        Assert.Equal(2, read.State.OccurrenceCount);
        Assert.Equal("0.1.1", read.State.LastSeenVersion);
    }

    [Fact]
    public async Task A_retried_report_id_is_a_duplicate_and_changes_nothing()
    {
        var ct = TestContext.Current.CancellationToken;
        var (store, _, handle) = await CreateAsync(ct);
        await using var _h = handle;
        var id = Guid.NewGuid();
        await store.CommitAsync(new CrashCommit(Report(id), null, NewGroup(), null, []), ct);
        var next = NewGroup();
        next = next with { State = next.State with { OccurrenceCount = 2 } };

        var status = await store.CommitAsync(new CrashCommit(Report(id), null, next, 1, []), ct);

        Assert.Equal(CrashCommitStatus.DuplicateReport, status);
        Assert.Equal(1, (await store.GetGroupAsync(_fp, ct))!.State.OccurrenceCount);
    }

    [Fact]
    public async Task A_file_issue_row_counts_against_the_day_and_blocks_a_second_one_until_it_is_parked()
    {
        var ct = TestContext.Current.CancellationToken;
        var (store, outbox, handle) = await CreateAsync(ct);
        await using var _h = handle;
        var row = Action(new FileIssue(_fp));
        await store.CommitAsync(new CrashCommit(Report(), null, NewGroup(), null, [row]), ct);

        Assert.Equal(1, await store.CountIssueFilingsAsync(DateOnly.FromDateTime(_t0.UtcDateTime), ct));
        Assert.Equal(0, await store.CountIssueFilingsAsync(DateOnly.FromDateTime(_t0.UtcDateTime).AddDays(1), ct));
        Assert.True(await store.HasPendingFileIssueAsync(_fp, ct));

        await outbox.DeadLetterAsync(row.Id, "gave up", ct);

        Assert.False(await store.HasPendingFileIssueAsync(_fp, ct));
        Assert.Equal(1, await store.CountIssueFilingsAsync(DateOnly.FromDateTime(_t0.UtcDateTime), ct));
    }

    [Fact]
    public async Task Outbox_rows_round_trip_and_leave_when_completed()
    {
        var ct = TestContext.Current.CancellationToken;
        var (store, outbox, handle) = await CreateAsync(ct);
        await using var _h = handle;
        var comment = Action(new AddComment(CommentReason.OccurrenceThreshold, 10, "Seen 10 times."), _t0.AddMinutes(1));
        var reopen = Action(new Reopen("0.1.0", "0.1.2", "Back in 0.1.2."), _t0.AddMinutes(2));
        await store.CommitAsync(new CrashCommit(Report(), null, NewGroup(), null, [comment, reopen]), ct);

        var due = await outbox.GetDueAsync(_t0.AddHours(1), 10, ct);

        Assert.Equal([comment.Id, reopen.Id], due.Select(d => d.Id));
        Assert.Equal(comment.Action, due[0].Action);
        Assert.Equal(reopen.Action, due[1].Action);

        await outbox.CompleteAsync(comment.Id, ct);

        Assert.Equal([reopen.Id], (await outbox.GetDueAsync(_t0.AddHours(2), 10, ct)).Select(d => d.Id));
    }

    [Fact]
    public async Task A_row_handed_out_is_leased_so_a_second_dispatcher_does_not_get_it()
    {
        var ct = TestContext.Current.CancellationToken;
        var (store, outbox, handle) = await CreateAsync(ct);
        await using var _h = handle;
        var row = Action(new AddComment(CommentReason.OccurrenceThreshold, 10, "x"));
        await store.CommitAsync(new CrashCommit(Report(), null, NewGroup(), null, [row]), ct);

        Assert.Single(await outbox.GetDueAsync(_t0.AddHours(1), 10, ct));
        Assert.Empty(await outbox.GetDueAsync(_t0.AddHours(1).AddMinutes(1), 10, ct));
        Assert.Single(await outbox.GetDueAsync(_t0.AddHours(1).AddMinutes(6), 10, ct));
    }

    [Fact]
    public async Task A_failed_row_waits_until_its_retry_time_and_counts_the_attempt()
    {
        var ct = TestContext.Current.CancellationToken;
        var (store, outbox, handle) = await CreateAsync(ct);
        await using var _h = handle;
        var row = Action(new AddComment(CommentReason.OccurrenceThreshold, 10, "x"));
        await store.CommitAsync(new CrashCommit(Report(), null, NewGroup(), null, [row]), ct);

        await outbox.FailAsync(row.Id, _t0.AddMinutes(5), "503", ct);

        Assert.Empty(await outbox.GetDueAsync(_t0.AddMinutes(4), 10, ct));
        var due = Assert.Single(await outbox.GetDueAsync(_t0.AddMinutes(5), 10, ct));
        Assert.Equal(1, due.Attempts);
    }

    [Fact]
    public async Task Only_deferred_groups_are_listed_oldest_first()
    {
        var ct = TestContext.Current.CancellationToken;
        var (store, _, handle) = await CreateAsync(ct);
        await using var _h = handle;
        var older = NewGroup("fp1:aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", GroupStatus.FilingDeferred);
        var newer = NewGroup("fp1:bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb", GroupStatus.FilingDeferred);
        newer = newer with { State = newer.State with { FirstSeenAt = _t0.AddHours(1) } };
        await store.CommitAsync(new CrashCommit(Report(fp: newer.State.Fingerprint), null, newer, null, []), ct);
        await store.CommitAsync(new CrashCommit(Report(fp: older.State.Fingerprint), null, older, null, []), ct);
        await store.CommitAsync(new CrashCommit(Report(), null, NewGroup(), null, []), ct);

        var list = await store.ListDeferredGroupsAsync(10, ct);

        Assert.Equal([older.State.Fingerprint, newer.State.Fingerprint], list.Select(g => g.State.Fingerprint));
    }

    [Fact]
    public async Task A_screenshot_is_stored_with_its_report()
    {
        var ct = TestContext.Current.CancellationToken;
        var (store, _, handle) = await CreateAsync(ct);
        await using var _h = handle;
        var report = Report();
        var shot = new StoredCrashAttachment(Guid.NewGuid(), report.ReportId, "image/png", new string('b', 64), new SealedPayload("key-1", [1], [2], [3]));

        var status = await store.CommitAsync(new CrashCommit(report, shot, NewGroup(), null, []), ct);

        Assert.Equal(CrashCommitStatus.Committed, status);
    }

    [Fact]
    public async Task A_late_screenshot_attaches_once_to_a_stored_report_and_never_to_an_unknown_one()
    {
        var ct = TestContext.Current.CancellationToken;
        var (store, _, handle) = await CreateAsync(ct);
        await using var _h = handle;
        var report = Report();
        await store.CommitAsync(new CrashCommit(report, null, NewGroup(), null, []), ct);
        var attachment = new StoredCrashAttachment(
            Guid.NewGuid(), report.ReportId, "image/png", new string('b', 64), new SealedPayload("key-1", [1, 2, 3], [4, 5, 6], [7, 8]));

        var first = await store.AttachLateAsync(attachment, ct);
        var second = await store.AttachLateAsync(attachment with { AttachmentId = Guid.NewGuid() }, ct);
        var unknown = await store.AttachLateAsync(attachment with { AttachmentId = Guid.NewGuid(), ReportId = Guid.NewGuid() }, ct);

        Assert.True(first);
        Assert.False(second);
        Assert.False(unknown);
    }
}

public sealed class InMemoryCrashStoreContractTests : CrashStoreContract
{
    protected override Task<(ICrashIntakeStore Store, ICrashActionOutbox Outbox, IAsyncDisposable Handle)> CreateAsync(CancellationToken ct)
    {
        var store = new InMemoryCrashStore();
        return Task.FromResult<(ICrashIntakeStore, ICrashActionOutbox, IAsyncDisposable)>((store, store, new NoopHandle()));
    }

    private sealed class NoopHandle : IAsyncDisposable
    {
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
