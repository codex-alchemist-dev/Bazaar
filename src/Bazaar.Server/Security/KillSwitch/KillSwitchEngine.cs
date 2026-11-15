namespace Bazaar.Server.Security.KillSwitch;

/// <summary>
/// Platform kill switch (doc 15, "Audit log and kill switch"; doc 48 section 11). It holds two kinds of stop: a global stop,
/// which denies write and egress tools for every agent, and per-agent stops, which deny every effect for one agent and call
/// <see cref="IAgentContainment"/> so the host can pause its VMs and revoke its leases. Read tools stay allowed under a global
/// stop, so the status page keeps working.
/// </summary>
/// <remarks>
/// The engine does not authorize callers. Host calls (<see cref="EngageGlobalAsync"/>, <see cref="ResumeGlobalAsync"/>,
/// <see cref="EngageAgentAsync"/>, <see cref="ResumeAgentAsync"/>) take an actor id that the caller has already checked
/// against the owner role. The breach path (<see cref="ReportBreachAsync"/>) is for the rate and budget code and uses the
/// fixed <see cref="BreachActorId"/>. Every engage and resume call writes one record to <see cref="IKillSwitchAuditSink"/>.
/// <para>
/// Stops are kept in an <see cref="IKillSwitchStateStore"/>. The engine loads it when constructed and keeps the loaded state as
/// a cache. Each engage and resume writes through the store before the cache changes, so a failed write leaves the stop as it
/// was. Until the first load succeeds, <see cref="Evaluate"/> denies every effect (fail closed); a failed load is retried by
/// the next state change, which waits for the load and throws if it still fails.
/// </para>
/// </remarks>
public sealed class KillSwitchEngine
{
    /// <summary>Actor id recorded for an automatic breach stop. It is not an owner.</summary>
    public const string BreachActorId = "system:breach";

    private const string _notLoadedReason = "Kill switch state is not loaded yet; effects are denied until it is.";

    private readonly IKillSwitchAuditSink _audit;
    private readonly IAgentContainment _containment;
    private readonly IKillSwitchStateStore _store;
    private readonly TimeProvider _time;
    private readonly Lock _gate = new();
    private readonly SerialLane _lane = new();
    private readonly SortedDictionary<string, KillSwitchStop> _agentStops = new(StringComparer.Ordinal);
    private KillSwitchStop? _global;
    private bool _loaded; // guarded by _gate
    private Task _loadTask; // guarded by _gate; replaced when a load fails

    /// <summary>Creates an engine with an in-memory state store, so stops last only as long as the process.</summary>
    /// <param name="audit">Receives one record per engage or resume.</param>
    /// <param name="containment">Host hook for agent stops.</param>
    /// <param name="timeProvider">Clock for the stop instants. Defaults to <see cref="TimeProvider.System"/>.</param>
    public KillSwitchEngine(IKillSwitchAuditSink audit, IAgentContainment containment, TimeProvider? timeProvider = null)
        : this(audit, containment, new InMemoryKillSwitchStateStore(), timeProvider)
    {
    }

    /// <summary>Creates an engine over a durable state store and loads the stops it holds.</summary>
    /// <param name="audit">Receives one record per engage or resume.</param>
    /// <param name="containment">Host hook for agent stops.</param>
    /// <param name="store">Durable stop state. Every engage and resume is written here first.</param>
    /// <param name="timeProvider">Clock for the stop instants. Defaults to <see cref="TimeProvider.System"/>.</param>
    public KillSwitchEngine(
        IKillSwitchAuditSink audit,
        IAgentContainment containment,
        IKillSwitchStateStore store,
        TimeProvider? timeProvider = null)
    {
        _audit = audit ?? throw new ArgumentNullException(nameof(audit));
        _containment = containment ?? throw new ArgumentNullException(nameof(containment));
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _time = timeProvider ?? TimeProvider.System;
        _loadTask = LoadStateAsync(CancellationToken.None);
    }

    /// <summary>
    /// Engages the global stop. Write and egress tools are denied for every agent until <see cref="ResumeGlobalAsync"/>.
    /// The stop is written to the store, then the cache is set, then the record is written, so a failed store write leaves
    /// the stop unchanged and a failed audit write leaves it in place.
    /// </summary>
    public async Task EngageGlobalAsync(string actorId, string reason, CancellationToken ct = default)
    {
        ValidateActorAndReason(actorId, reason);
        await EnsureLoadedAsync(ct).ConfigureAwait(false);

        var now = _time.GetUtcNow();
        var stop = new KillSwitchStop(KillSwitchScope.Global, null, actorId, reason, now);
        await _lane.RunAsync(async () =>
        {
            await _store.SetGlobalAsync(stop, ct).ConfigureAwait(false);
            lock (_gate)
            {
                _global = stop;
            }
        }, ct).ConfigureAwait(false);

        await _audit.RecordAsync(new KillSwitchAuditEntry(now, actorId, KillSwitchAction.Engaged, KillSwitchScope.Global, null, reason), ct)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Resumes the global stop. The record is written before the state is lifted, so a resume that cannot be audited does not
    /// happen. The stored stop is removed before the cache is cleared. Agent stops are not affected.
    /// </summary>
    public async Task ResumeGlobalAsync(string actorId, string reason, CancellationToken ct = default)
    {
        ValidateActorAndReason(actorId, reason);
        await EnsureLoadedAsync(ct).ConfigureAwait(false);

        var now = _time.GetUtcNow();
        await _audit.RecordAsync(new KillSwitchAuditEntry(now, actorId, KillSwitchAction.Resumed, KillSwitchScope.Global, null, reason), ct)
            .ConfigureAwait(false);

        await _lane.RunAsync(async () =>
        {
            await _store.ClearGlobalAsync(ct).ConfigureAwait(false);
            lock (_gate)
            {
                _global = null;
            }
        }, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Engages a stop for one agent. The stop is written to the store, then the cache is set, then the record is written, then
    /// the host hook runs. A failure in the store leaves the agent as it was; a failure in the record or the hook leaves the agent
    /// denied, and calling again repeats the (idempotent) hook.
    /// </summary>
    public async Task EngageAgentAsync(string actorId, string agentId, string reason, CancellationToken ct = default)
    {
        ValidateActorAndReason(actorId, reason);
        ArgumentException.ThrowIfNullOrWhiteSpace(agentId);
        await EnsureLoadedAsync(ct).ConfigureAwait(false);

        var now = _time.GetUtcNow();
        var stop = new KillSwitchStop(KillSwitchScope.Agent, agentId, actorId, reason, now);
        await _lane.RunAsync(async () =>
        {
            await _store.SetAgentStopAsync(stop, ct).ConfigureAwait(false);
            lock (_gate)
            {
                _agentStops[agentId] = stop;
            }
        }, ct).ConfigureAwait(false);

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
        await EnsureLoadedAsync(ct).ConfigureAwait(false);

        var now = _time.GetUtcNow();
        await _audit.RecordAsync(new KillSwitchAuditEntry(now, actorId, KillSwitchAction.Resumed, KillSwitchScope.Agent, agentId, reason), ct)
            .ConfigureAwait(false);
        var release = await _containment.ReleaseAsync(agentId, request, ct).ConfigureAwait(false);
        if (release.Outcome == ContainmentReleaseOutcome.Denied)
        {
            // The agent stays denied: only the owner may lift containment.
            throw new UnauthorizedAccessException("Only the owner may resume a contained agent.");
        }

        await _lane.RunAsync(async () =>
        {
            await _store.ClearAgentStopAsync(agentId, ct).ConfigureAwait(false);
            lock (_gate)
            {
                _agentStops.Remove(agentId);
            }
        }, ct).ConfigureAwait(false);
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
    /// under a global stop. Until the state is loaded every effect is denied. This method does no I/O and is safe to call from
    /// the dispatch path.
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
            if (!_loaded)
            {
                return KillSwitchDecision.Deny(KillSwitchScope.Global, _notLoadedReason);
            }

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

    /// <summary>Reads the stored stops into the cache. Runs once from the constructor, and again after a failed load.</summary>
    private async Task LoadStateAsync(CancellationToken ct)
    {
        var status = await _store.LoadAsync(ct).ConfigureAwait(false);
        lock (_gate)
        {
            _global = status.Global;
            _agentStops.Clear();
            foreach (var stop in status.AgentStops)
            {
                _agentStops[stop.AgentId!] = stop;
            }
            _loaded = true;
        }
    }

    /// <summary>Waits for the state load. A load that failed is started again, so a transient store outage does not stay fatal.</summary>
    private async Task EnsureLoadedAsync(CancellationToken ct)
    {
        Task pending;
        lock (_gate)
        {
            if (_loaded)
            {
                return;
            }
            if (_loadTask.IsFaulted || _loadTask.IsCanceled)
            {
                _loadTask = LoadStateAsync(CancellationToken.None);
            }
            pending = _loadTask;
        }

        await pending.WaitAsync(ct).ConfigureAwait(false);
    }

    private static void ValidateActorAndReason(string actorId, string reason)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(actorId, nameof(actorId));
        ArgumentException.ThrowIfNullOrWhiteSpace(reason, nameof(reason));
    }
}
