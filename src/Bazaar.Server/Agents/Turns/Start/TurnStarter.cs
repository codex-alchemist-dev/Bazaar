using Bazaar.Server.Agents.Data;

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
    TurnStarterOptions options)
{
    private const string _ryuseiPrefix = "ryusei_";

    /// <summary>Starts one turn for the due items of the request's agent, or returns <see cref="TurnStartResult.NothingDue"/>.</summary>
    /// <exception cref="ArgumentException">The holder or route is empty.</exception>
    /// <exception cref="ArgumentOutOfRangeException">MaxItems is below 1.</exception>
    public async Task<TurnStartResult> StartAsync(TurnStartRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.Holder);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.Route);
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

        var outcome = await runner.RunAsync(turn, ct).ConfigureAwait(false);
        return new TurnStartResult(true, outcome, ids, wakeReason, depth);
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
