namespace Bazaar.Server.Agents.Turns.Subagents;

/// <summary>
/// Runs child turn loops for a parent turn under the depth, tool and concurrency limits (doc 06 "Subagents", WP8). The child
/// runs through the same <see cref="ITurnRunner"/> as any turn, with its own turn id, lease holder and tool allowlist.
/// </summary>
public sealed class SubagentRunner
{
    private readonly ITurnRunner _turns;
    private readonly SubagentOptions _options;
    private readonly Lock _gate = new();
    private readonly Dictionary<Guid, int> _running = [];

    /// <param name="turns">The parent turn loop that runs each child.</param>
    /// <param name="options">The limits. Validated once here.</param>
    /// <exception cref="ArgumentOutOfRangeException">A limit in <paramref name="options"/> is below 1.</exception>
    public SubagentRunner(ITurnRunner turns, SubagentOptions options)
    {
        ArgumentNullException.ThrowIfNull(turns);
        ArgumentNullException.ThrowIfNull(options);
        options.Validate();
        _turns = turns;
        _options = options;
    }

    /// <summary>
    /// Checks the limits, then runs the child to its end and returns its summary. The limit checks run before anything starts,
    /// so a rejected request leaves no turn behind.
    /// </summary>
    /// <exception cref="SubagentRejectedException">A depth, tool or concurrency limit is broken.</exception>
    public async Task<SubagentResult> SpawnAsync(SubagentRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentOutOfRangeException.ThrowIfNegative(request.ParentDepth);
        ArgumentNullException.ThrowIfNull(request.ParentToolAllowlist);
        ArgumentNullException.ThrowIfNull(request.ToolAllowlist);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.ParentHolder);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.Prompt);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.Route);
        if (request.ParentTurnId == Guid.Empty)
            throw new ArgumentException("A subagent request needs the parent turn id.", nameof(request));
        if (request.ParentStepCap is { } parentCap)
            ArgumentOutOfRangeException.ThrowIfLessThan(parentCap, 1);

        var childDepth = request.ParentDepth + 1;
        if (childDepth > _options.MaxDepth)
            throw new SubagentRejectedException(
                SubagentRejectionCodes.DepthLimit,
                $"Child depth {childDepth} exceeds the limit of {_options.MaxDepth}.");

        if (!IsSubset(request.ToolAllowlist, request.ParentToolAllowlist))
            throw new SubagentRejectedException(
                SubagentRejectionCodes.ToolNotInParent,
                "The child's tool allowlist names a tool the parent does not hold.");

        ReserveSlot(request.ParentTurnId);
        try
        {
            // Children do not post unless the operator allows it. The trigger GC is handed over only then. The turn runner
            // must treat a null TriggerGcId as "no post target"; that rule lives in the runner, not in this package.
            var canPost = _options.MayPost;

            // The child never gets more steps than its parent, nor more than the operator's subagent ceiling.
            var stepCap = Math.Min(request.ParentStepCap ?? _options.StepCap, _options.StepCap);

            var runRequest = new TurnRunRequest(
                AgentId: request.AgentId,
                SessionId: request.SessionId,
                Role: TurnRole.Actor,
                WakeReason: "subagent",
                InboxItemIds: [],
                Holder: $"{request.ParentHolder}/sub-{Guid.NewGuid():D}",
                Route: request.Route,
                ToolAllowlist: request.ToolAllowlist.ToArray(),
                ParentTurnId: request.ParentTurnId,
                Depth: childDepth,
                TriggerGcId: canPost ? request.ParentTriggerGcId : null,
                TriggerGcSeq: canPost ? request.ParentTriggerGcSeq : null,
                StepCapOverride: stepCap);

            var outcome = await _turns.RunAsync(runRequest, cancellationToken);
            return new SubagentResult(outcome.TurnId, outcome.Reason, outcome.Summary, outcome.StepCount);
        }
        finally
        {
            ReleaseSlot(request.ParentTurnId);
        }
    }

    private void ReserveSlot(Guid parentTurnId)
    {
        lock (_gate)
        {
            _running.TryGetValue(parentTurnId, out var count);
            if (count >= _options.MaxConcurrent)
                throw new SubagentRejectedException(
                    SubagentRejectionCodes.ConcurrencyLimit,
                    $"The parent turn already has {count} children running; the limit is {_options.MaxConcurrent}.");
            _running[parentTurnId] = count + 1;
        }
    }

    private void ReleaseSlot(Guid parentTurnId)
    {
        lock (_gate)
        {
            if (!_running.TryGetValue(parentTurnId, out var count))
                return;
            if (count <= 1)
                _running.Remove(parentTurnId);
            else
                _running[parentTurnId] = count - 1;
        }
    }

    private static bool IsSubset(IReadOnlyList<string> child, IReadOnlyList<string> parent)
    {
        var allowed = new HashSet<string>(parent, StringComparer.Ordinal);
        return child.All(allowed.Contains);
    }
}
