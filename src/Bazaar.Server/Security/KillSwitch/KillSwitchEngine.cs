namespace Bazaar.Server.Security.KillSwitch;

/// <summary>
/// Platform kill switch (doc 15, "Audit log and kill switch"; doc 48 section 11). It holds two kinds of stop in memory:
/// a global stop, which denies write and egress tools for every agent, and per-agent stops, which deny every effect for one
/// agent and call <see cref="IAgentContainment"/> so the host can pause its VMs and revoke its leases. Read tools stay
/// allowed under a global stop, so the status page keeps working.
/// </summary>
/// <remarks>
/// The engine does not authorize callers. Host calls (<see cref="EngageGlobalAsync"/>, <see cref="ResumeGlobalAsync"/>,
/// <see cref="EngageAgentAsync"/>, <see cref="ResumeAgentAsync"/>) take an actor id that the caller has already checked
/// against the owner role. The breach path (<see cref="ReportBreachAsync"/>) is for the rate and budget code and uses the
/// fixed <see cref="BreachActorId"/>. Every engage and resume call writes one record to <see cref="IKillSwitchAuditSink"/>.
/// Stop state is process-local and is cleared on restart; the host must re-engage stops it needs after a restart.
/// </remarks>
public sealed class KillSwitchEngine(IKillSwitchAuditSink audit, IAgentContainment containment, TimeProvider? timeProvider = null)
{
    /// <summary>Actor id recorded for an automatic breach stop. It is not an owner.</summary>
    public const string BreachActorId = "system:breach";

    private readonly IKillSwitchAuditSink _audit = audit ?? throw new ArgumentNullException(nameof(audit));
    private readonly IAgentContainment _containment = containment ?? throw new ArgumentNullException(nameof(containment));
    private readonly TimeProvider _time = timeProvider ?? TimeProvider.System;
    private readonly Lock _gate = new();
    private readonly SortedDictionary<string, KillSwitchStop> _agentStops = new(StringComparer.Ordinal);
    private KillSwitchStop? _global;

    /// <summary>
    /// Engages the global stop. Write and egress tools are denied for every agent until <see cref="ResumeGlobalAsync"/>.
    /// The state is set first, then the record is written, so a failed write leaves the stop in place.
    /// </summary>
    public async Task EngageGlobalAsync(string actorId, string reason, CancellationToken ct = default)
    {
        ValidateActorAndReason(actorId, reason);

        var now = _time.GetUtcNow();
        lock (_gate)
        {
            _global = new KillSwitchStop(KillSwitchScope.Global, null, actorId, reason, now);
        }

        await _audit.RecordAsync(new KillSwitchAuditEntry(now, actorId, KillSwitchAction.Engaged, KillSwitchScope.Global, null, reason), ct)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Resumes the global stop. The record is written before the state is lifted, so a resume that cannot be audited does not
    /// happen. Agent stops are not affected.
    /// </summary>
    public async Task ResumeGlobalAsync(string actorId, string reason, CancellationToken ct = default)
    {
        ValidateActorAndReason(actorId, reason);

        var now = _time.GetUtcNow();
        await _audit.RecordAsync(new KillSwitchAuditEntry(now, actorId, KillSwitchAction.Resumed, KillSwitchScope.Global, null, reason), ct)
            .ConfigureAwait(false);

        lock (_gate)
        {
            _global = null;
        }
    }

    /// <summary>
    /// Engages a stop for one agent. The state is set first, then the record is written, then the host hook runs. A failure
    /// in the record or the hook leaves the agent denied; calling again repeats the (idempotent) hook.
    /// </summary>
    public async Task EngageAgentAsync(string actorId, string agentId, string reason, CancellationToken ct = default)
    {
        ValidateActorAndReason(actorId, reason);
        ArgumentException.ThrowIfNullOrWhiteSpace(agentId);

        var now = _time.GetUtcNow();
        lock (_gate)
        {
            _agentStops[agentId] = new KillSwitchStop(KillSwitchScope.Agent, agentId, actorId, reason, now);
        }

        await _audit.RecordAsync(new KillSwitchAuditEntry(now, actorId, KillSwitchAction.Engaged, KillSwitchScope.Agent, agentId, reason), ct)
            .ConfigureAwait(false);
        await _containment.ContainAsync(agentId, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Resumes an agent stop with a request signed by an owner device (see <see cref="IAgentContainment.IssueReleaseChallengeAsync"/>).
    /// The record is written first, then the host hook verifies the request and releases containment, then the state is lifted.
    /// If the hook fails or denies the request, the agent stays denied. Leases revoked by the engage are not restored here.
    /// </summary>
    public async Task ResumeAgentAsync(string agentId, string reason, ContainmentReleaseRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var actorId = request.DeviceId;
        ValidateActorAndReason(actorId, reason);
        ArgumentException.ThrowIfNullOrWhiteSpace(agentId);

        var now = _time.GetUtcNow();
        await _audit.RecordAsync(new KillSwitchAuditEntry(now, actorId, KillSwitchAction.Resumed, KillSwitchScope.Agent, agentId, reason), ct)
            .ConfigureAwait(false);
        var release = await _containment.ReleaseAsync(agentId, request, ct).ConfigureAwait(false);
        if (release.Outcome == ContainmentReleaseOutcome.Denied)
        {
            // The agent stays denied: only the owner may lift containment.
            throw new UnauthorizedAccessException("Only the owner may resume a contained agent.");
        }

        lock (_gate)
        {
            _agentStops.Remove(agentId);
        }
    }

    /// <summary>
    /// Automatic breach stop for one agent, called by the rate or budget code. It engages an agent stop with
    /// <see cref="BreachActorId"/> as the actor. It never engages the global stop.
    /// </summary>
    public Task ReportBreachAsync(string agentId, string reason, CancellationToken ct = default) =>
        EngageAgentAsync(BreachActorId, agentId, reason, ct);

    /// <summary>
    /// Decides whether a tool call with the given effect may run for the agent. An agent stop denies every effect for that agent.
    /// A global stop denies <see cref="ToolEffect.Write"/> and <see cref="ToolEffect.Egress"/> for all agents. Reads are allowed
    /// under a global stop. This method does no I/O and is safe to call from the dispatch path.
    /// </summary>
    /// <param name="agentId">The agent making the call. Must not be blank.</param>
    /// <param name="effect">The effect class of the tool.</param>
    public KillSwitchDecision Evaluate(string agentId, ToolEffect effect)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(agentId);
        if (!Enum.IsDefined(effect))
        {
            throw new ArgumentOutOfRangeException(nameof(effect), effect, "Unknown tool effect.");
        }

        lock (_gate)
        {
            if (_agentStops.TryGetValue(agentId, out var agentStop))
            {
                return KillSwitchDecision.Deny(KillSwitchScope.Agent, agentStop.Reason);
            }

            if (effect != ToolEffect.Read && _global is { } global)
            {
                return KillSwitchDecision.Deny(KillSwitchScope.Global, global.Reason);
            }
        }

        return KillSwitchDecision.Allow;
    }

    /// <summary>Read-only snapshot of the current stops. Always allowed, including under a global stop.</summary>
    public KillSwitchStatus GetStatus()
    {
        lock (_gate)
        {
            return new KillSwitchStatus(_global, _agentStops.Values.ToArray());
        }
    }

    private static void ValidateActorAndReason(string actorId, string reason)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(actorId, nameof(actorId));
        ArgumentException.ThrowIfNullOrWhiteSpace(reason, nameof(reason));
    }
}
