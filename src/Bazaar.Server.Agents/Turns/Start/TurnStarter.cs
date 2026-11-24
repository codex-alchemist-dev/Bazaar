using Bazaar.Server.Agents.Data;
using Bazaar.Server.Agents.Inbox;

namespace Bazaar.Server.Agents.Turns.Start;

/// <summary>
/// Turns due inbox items into one <see cref="ITurnRunner.RunAsync"/> call (doc 06, "Wake model" and "Turn loop" step 1; doc 38,
/// Chain depth). It peeks the due items, reads their chain depths and starts one turn for the batch. It claims nothing itself:
/// the runner claims the peeked items by id.
/// </summary>
/// <remarks>
/// The wake reason is the kind of the highest-priority item. The chain depth is the largest depth any item reports; a
/// <c>ryusei_*</c> item whose depth cannot be read counts as <see cref="TurnStarterOptions.UnknownRyuseiDepth"/>, never as 0.
/// </remarks>
public sealed class TurnStarter(
    IDueInboxReader inbox,
    IWakeChainDepthSource depths,
    ITurnRunner runner,
    TurnStarterOptions options,
    IEnumerable<ITaskSessionHook>? taskHooks = null)
{
    private const string _ryuseiPrefix = "ryusei_";
    private readonly ITaskSessionHook[] _taskHooks = taskHooks?.ToArray() ?? [];

    /// <summary>Starts one turn for the due items of the request's agent, or returns <see cref="TurnStartResult.NothingDue"/>.</summary>
    /// <exception cref="ArgumentException">The holder or route is empty.</exception>
    /// <exception cref="ArgumentOutOfRangeException">MaxItems is below 1.</exception>
    public async Task<TurnStartResult> StartAsync(TurnStartRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.Holder);
        // A blank route is valid: the gateway then resolves the agent's own policy default route (doc 05, per-agent routing).
        ArgumentNullException.ThrowIfNull(request.Route);
        ArgumentOutOfRangeException.ThrowIfLessThan(request.MaxItems, 1);

        var due = await inbox.PeekDueAsync(request.AgentId, request.MaxItems, ct).ConfigureAwait(false);
        if (due.Count == 0)
            return TurnStartResult.NothingDue;

        // The peek is in claim order, so the first item is the highest priority.
        var wakeReason = due[0].Kind;
        var depth = await ReadChainDepthAsync(due, ct).ConfigureAwait(false);

        // The first item that is backed by a GC line is the trigger line of the turn (doc 06, GC backread).
        var trigger = due.FirstOrDefault(item => item.GcId.HasValue && item.GcSeq.HasValue);
        var ids = due.Select(item => item.InboxItemId).ToList();

        var turn = new TurnRunRequest(
            request.AgentId,
            request.SessionId,
            request.Role,
            wakeReason,
            ids,
            request.Holder,
            request.Route,
            request.ToolAllowlist,
            TriggerGcId: trigger?.GcId?.ToString("D"),
            TriggerGcSeq: trigger?.GcSeq,
            ChainDepth: depth);

        // A task wake (a card assignment whose source is the card id) starts the task session hooks before the first turn.
        var task = due.FirstOrDefault(IsTaskItem);
        if (task is not null)
            await StartTaskSessionAsync(request, trigger, task, ct).ConfigureAwait(false);

        var outcome = await runner.RunAsync(turn, ct).ConfigureAwait(false);
        return new TurnStartResult(true, outcome, ids, wakeReason, depth);
    }

    /// <summary>A card assignment whose source ref is a card id. The kind is <see cref="InboxKind.Assignment"/> (doc 12).</summary>
    private static bool IsTaskItem(InboxRow item) =>
        item.Kind == InboxKind.Assignment && Guid.TryParse(item.SourceRef, out _);

    /// <summary>Calls every task hook's StartAsync. A hook that reports an already held worktree (InvalidOperationException) is skipped.</summary>
    private async Task StartTaskSessionAsync(TurnStartRequest request, InboxRow? trigger, InboxRow task, CancellationToken ct)
    {
        var start = new TaskSessionStart(
            request.SessionId,
            request.AgentId,
            RepoRef: null,
            TaskSlug: null,
            GcId: trigger?.GcId,
            TaskId: Guid.Parse(task.SourceRef!));

        foreach (var hook in _taskHooks)
        {
            try
            {
                await hook.StartAsync(start, ct).ConfigureAwait(false);
            }
            catch (InvalidOperationException)
            {
                // The session already holds a worktree; the coding side documents this as the signal to continue.
            }
        }
    }

    /// <summary>The largest depth any item reports, with unreadable ryusei depths counted as the fail-closed value.</summary>
    private async Task<int> ReadChainDepthAsync(IReadOnlyList<InboxRow> due, CancellationToken ct)
    {
        var depth = 0;
        foreach (var item in due)
        {
            var itemDepth = await depths.ChainDepthAsync(item.Kind, item.SourceRef, ct).ConfigureAwait(false);
            if (itemDepth is null && item.Kind.StartsWith(_ryuseiPrefix, StringComparison.Ordinal))
                itemDepth = options.UnknownRyuseiDepth;
            if (itemDepth is { } value)
                depth = Math.Max(depth, value);
        }

        return depth;
    }
}
