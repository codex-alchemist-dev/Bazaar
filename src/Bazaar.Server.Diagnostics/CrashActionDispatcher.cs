using Bazaar.Diagnostics.Grouping;
using Bazaar.Server.Security.Audit;

namespace Bazaar.Server.Diagnostics;

/// <summary>What one dispatch pass did, for the operator's health view and for tests.</summary>
/// <param name="Completed">Outbox rows carried out or found already done.</param>
/// <param name="Failed">Rows that failed and were rescheduled.</param>
/// <param name="DeadLettered">Rows parked after too many failures.</param>
/// <param name="Released">Deferred groups released into the outbox by the budget.</param>
public sealed record CrashDispatchSummary(int Completed, int Failed, int DeadLettered, int Released);

/// <summary>
/// Carries out the filing outbox (doc 43 section 8.4): create an issue, comment, reopen. It is safe to run more than once at a
/// time and after a crash: a row is only removed after GitHub accepted it, an already-filed group is not filed twice, and a
/// failure backs off instead of dropping the row. Deferred groups are released here when the daily budget allows.
/// </summary>
public sealed class CrashActionDispatcher
{
    private static readonly TimeSpan _firstBackoff = TimeSpan.FromMinutes(1);
    private static readonly TimeSpan _maxBackoff = TimeSpan.FromHours(6);

    private readonly ICrashActionOutbox _outbox;
    private readonly ICrashIntakeStore _store;
    private readonly ICrashIssueFiler _filer;
    private readonly IAuditLog _audit;
    private readonly TimeProvider _clock;
    private readonly CrashFilingOptions _options;

    /// <summary>Creates the dispatcher over its seams.</summary>
    public CrashActionDispatcher(
        ICrashActionOutbox outbox,
        ICrashIntakeStore store,
        ICrashIssueFiler filer,
        IAuditLog audit,
        TimeProvider clock,
        CrashFilingOptions options)
    {
        ArgumentNullException.ThrowIfNull(outbox);
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(filer);
        ArgumentNullException.ThrowIfNull(audit);
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentNullException.ThrowIfNull(options);
        _outbox = outbox;
        _store = store;
        _filer = filer;
        _audit = audit;
        _clock = clock;
        _options = options;
    }

    /// <summary>Runs one pass: release deferred groups the budget allows, then carry out up to <paramref name="max"/> due rows.</summary>
    public async Task<CrashDispatchSummary> RunOnceAsync(int max = 50, CancellationToken ct = default)
    {
        var released = _options.FilingEnabled ? await ReleaseDeferredAsync(ct).ConfigureAwait(false) : 0;
        if (!_options.FilingEnabled)
        {
            return new CrashDispatchSummary(0, 0, 0, released);
        }

        var now = _clock.GetUtcNow();
        var completed = 0;
        var failed = 0;
        var dead = 0;
        foreach (var row in await _outbox.GetDueAsync(now, max, ct).ConfigureAwait(false))
        {
            try
            {
                await CarryOutAsync(row, now, ct).ConfigureAwait(false);
                await _outbox.CompleteAsync(row.Id, ct).ConfigureAwait(false);
                completed++;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
#pragma warning disable CA1031 // Any failure of the filer must reschedule the row, never lose it or stop the pass.
            catch (Exception ex)
#pragma warning restore CA1031
            {
                var attempts = row.Attempts + 1;
                if (attempts >= _options.MaxActionAttempts)
                {
                    await _outbox.DeadLetterAsync(row.Id, ex.GetType().Name, ct).ConfigureAwait(false);
                    await _audit.AppendAsync(CrashAudit.ActionDead(now, row.Fingerprint, row.Action.GetType().Name, attempts), ct).ConfigureAwait(false);
                    dead++;
                }
                else
                {
                    await _outbox.FailAsync(row.Id, now + Backoff(attempts), ex.GetType().Name, ct).ConfigureAwait(false);
                    failed++;
                }
            }
        }

        return new CrashDispatchSummary(completed, failed, dead, released);
    }

    private async Task CarryOutAsync(PendingCrashAction row, DateTimeOffset now, CancellationToken ct)
    {
        var group = await _store.GetGroupAsync(row.Fingerprint, ct).ConfigureAwait(false);
        if (group is null)
        {
            return;
        }

        switch (row.Action)
        {
            case FileIssue when group.State.IssueNumber is not null:
                return;

            case FileIssue:
                var text = IssueBody.Render(new IssueContent(
                    group.State, group.Facts.ErrorCode, group.Facts.ErrorType, group.Facts.Frames, group.Facts.FlaggedLine, group.Facts.RedactedMessage, _options.FiledBy));
                var number = await _filer.CreateIssueAsync(text, ct).ConfigureAwait(false);
                await UpdateGroupAsync(row.Fingerprint, state => state.WithIssueNumber(number), ct).ConfigureAwait(false);
                await _audit.AppendAsync(CrashAudit.IssueEvent(now, "crash.issue.created", row.Fingerprint, number), ct).ConfigureAwait(false);
                return;

            case AddComment comment when group.State.IssueNumber is { } issue:
                await RunOnIssueAsync(row.Fingerprint, issue, now, () => _filer.CommentAsync(issue, comment.Text, ct), "crash.issue.commented", ct).ConfigureAwait(false);
                return;

            case Reopen reopen when group.State.IssueNumber is { } issue:
                await RunOnIssueAsync(row.Fingerprint, issue, now, () => _filer.ReopenAsync(issue, reopen.Text, ct), "crash.issue.reopened", ct).ConfigureAwait(false);
                return;

            default:
                // A comment or reopen for a group with no issue (deleted by hand since) has nothing to act on.
                return;
        }
    }

    private async Task RunOnIssueAsync(string fingerprint, int issue, DateTimeOffset now, Func<Task> act, string auditKind, CancellationToken ct)
    {
        try
        {
            await act().ConfigureAwait(false);
            await _audit.AppendAsync(CrashAudit.IssueEvent(now, auditKind, fingerprint, issue), ct).ConfigureAwait(false);
        }
        catch (CrashIssueNotFoundException)
        {
            // Deleted by hand: forget the number so the next occurrence files a new issue (doc 43 section 8.4).
            await UpdateGroupAsync(fingerprint, state => state.WithIssueDeleted(), ct).ConfigureAwait(false);
            await _audit.AppendAsync(CrashAudit.IssueDeleted(now, fingerprint, issue), ct).ConfigureAwait(false);
        }
    }

    private async Task UpdateGroupAsync(string fingerprint, Func<GroupState, GroupState> change, CancellationToken ct)
    {
        for (var attempt = 0; attempt < _options.MaxCommitAttempts; attempt++)
        {
            var current = await _store.GetGroupAsync(fingerprint, ct).ConfigureAwait(false);
            if (current is null)
            {
                return;
            }

            var commit = new CrashCommit(null, null, current with { State = change(current.State), Version = current.Version + 1 }, current.Version, []);
            if (await _store.CommitAsync(commit, ct).ConfigureAwait(false) == CrashCommitStatus.Committed)
            {
                return;
            }
        }

        throw new CrashIntakeContentionException(fingerprint);
    }

    private async Task<int> ReleaseDeferredAsync(CancellationToken ct)
    {
        var now = _clock.GetUtcNow();
        var filed = await _store.CountIssueFilingsAsync(DateOnly.FromDateTime(now.UtcDateTime), ct).ConfigureAwait(false);
        var room = _options.IssuesPerDay - filed;
        if (room <= 0)
        {
            return 0;
        }

        var released = 0;
        foreach (var group in await _store.ListDeferredGroupsAsync(room, ct).ConfigureAwait(false))
        {
            var next = group with { State = group.State with { Status = GroupStatus.Open }, Version = group.Version + 1 };
            var action = new PendingCrashAction(Guid.NewGuid(), group.State.Fingerprint, new FileIssue(group.State.Fingerprint), now, 0, now);
            if (await _store.CommitAsync(new CrashCommit(null, null, next, group.Version, [action]), ct).ConfigureAwait(false) == CrashCommitStatus.Committed)
            {
                released++;
            }
        }

        return released;
    }

    private static TimeSpan Backoff(int attempts)
    {
        var minutes = _firstBackoff.TotalMinutes * Math.Pow(2, attempts - 1);
        return TimeSpan.FromMinutes(Math.Min(minutes, _maxBackoff.TotalMinutes));
    }
}
