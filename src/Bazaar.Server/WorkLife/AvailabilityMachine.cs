namespace Bazaar.Server.WorkLife;

/// <summary>
/// States of the availability machine (doc 09, "Availability states"). Named apart from
/// <see cref="AvailabilityState"/>, which the schedule calculator produces.
/// </summary>
public enum AvailabilityMachineState
{
    /// <summary>Working inside a shift.</summary>
    OnShift,

    /// <summary>Finishing in-flight work and writing the handover note.</summary>
    Draining,

    /// <summary>Taking a break after a drain.</summary>
    Break,

    /// <summary>Outside every shift, or in curfew.</summary>
    Off,

    /// <summary>Covered by a vacation grant or a provider spend cap with no allowed source left.</summary>
    Vacation,
}

/// <summary>
/// Overlays that sit on top of the state. An overlay never changes the state.
/// </summary>
public abstract record AvailabilityOverlay
{
    /// <summary>Provider rate limit: the agent is throttled until the wait ends.</summary>
    /// <param name="Until">When the throttle is expected to end.</param>
    public sealed record Throttled(DateTimeOffset Until) : AvailabilityOverlay;

    /// <summary>A cover agent is answering for this agent.</summary>
    /// <param name="CoverAgentId">The agent covering for this one.</param>
    public sealed record Covered(string CoverAgentId) : AvailabilityOverlay;
}

/// <summary>
/// Availability of one agent at one point in its machine's history.
/// </summary>
/// <param name="State">Current state.</param>
/// <param name="Overlays">Current overlays, at most one <see cref="AvailabilityOverlay.Throttled"/> and one <see cref="AvailabilityOverlay.Covered"/>.</param>
/// <param name="Version">Starts at 0 and increases by one on each accepted transition.</param>
/// <param name="Reason">Why the last accepted transition happened, or <c>initial</c>.</param>
/// <param name="ProviderResetAt">When the provider cap that caused the last vacation resets; null if none.</param>
public sealed record AvailabilitySnapshot(
    AvailabilityMachineState State,
    IReadOnlyList<AvailabilityOverlay> Overlays,
    long Version,
    string Reason,
    DateTimeOffset? ProviderResetAt = null);

/// <summary>
/// Inputs to the machine. Every event carries the instant it is scheduled for, which forms part of
/// its idempotency key (agent, event name, scheduled instant).
/// </summary>
public abstract record AvailabilityEvent
{
    /// <summary>Creates an event.</summary>
    /// <param name="scheduledAt">The instant this event is scheduled for.</param>
    protected AvailabilityEvent(DateTimeOffset scheduledAt) => ScheduledAt = scheduledAt;

    /// <summary>The instant this event is scheduled for.</summary>
    public DateTimeOffset ScheduledAt { get; }

    /// <summary>Stable event name used in the idempotency key.</summary>
    public abstract string EventName { get; }

    /// <summary>The shift, break or vacation boundary ended.</summary>
    public sealed record ShiftEnd : AvailabilityEvent
    {
        /// <summary>Creates the event.</summary>
        public ShiftEnd(DateTimeOffset scheduledAt) : base(scheduledAt) { }

        /// <inheritdoc />
        public override string EventName => nameof(ShiftEnd);
    }

    /// <summary>A break is due.</summary>
    public sealed record BreakDue : AvailabilityEvent
    {
        /// <summary>Creates the event.</summary>
        public BreakDue(DateTimeOffset scheduledAt) : base(scheduledAt) { }

        /// <inheritdoc />
        public override string EventName => nameof(BreakDue);
    }

    /// <summary>A vacation grant starts.</summary>
    public sealed record VacationStarts : AvailabilityEvent
    {
        /// <summary>Creates the event.</summary>
        public VacationStarts(DateTimeOffset scheduledAt) : base(scheduledAt) { }

        /// <inheritdoc />
        public override string EventName => nameof(VacationStarts);
    }

    /// <summary>A token-driven break fires.</summary>
    public sealed record TokenBreak : AvailabilityEvent
    {
        /// <summary>Creates the event.</summary>
        public TokenBreak(DateTimeOffset scheduledAt) : base(scheduledAt) { }

        /// <inheritdoc />
        public override string EventName => nameof(TokenBreak);
    }

    /// <summary>The drain checklist finished; <paramref name="Target"/> is where the agent goes next.</summary>
    public sealed record DrainComplete : AvailabilityEvent
    {
        /// <summary>Creates the event.</summary>
        public DrainComplete(DateTimeOffset scheduledAt, AvailabilityMachineState target) : base(scheduledAt)
        {
            Target = target;
        }

        /// <summary>Target state: <see cref="AvailabilityMachineState.Break"/>, <see cref="AvailabilityMachineState.Off"/> or <see cref="AvailabilityMachineState.Vacation"/>.</summary>
        public AvailabilityMachineState Target { get; }

        /// <inheritdoc />
        public override string EventName => nameof(DrainComplete);
    }

    /// <summary>A scheduled shift starts.</summary>
    public sealed record ScheduleStart : AvailabilityEvent
    {
        /// <summary>Creates the event.</summary>
        public ScheduleStart(DateTimeOffset scheduledAt) : base(scheduledAt) { }

        /// <inheritdoc />
        public override string EventName => nameof(ScheduleStart);
    }

    /// <summary>A break ends.</summary>
    public sealed record BreakEnd : AvailabilityEvent
    {
        /// <summary>Creates the event.</summary>
        public BreakEnd(DateTimeOffset scheduledAt) : base(scheduledAt) { }

        /// <inheritdoc />
        public override string EventName => nameof(BreakEnd);
    }

    /// <summary>The provider rate-limited the agent until <paramref name="Until"/>.</summary>
    public sealed record RateLimited : AvailabilityEvent
    {
        /// <summary>Creates the event.</summary>
        public RateLimited(DateTimeOffset scheduledAt, DateTimeOffset until) : base(scheduledAt)
        {
            Until = until;
        }

        /// <summary>When the wait ends.</summary>
        public DateTimeOffset Until { get; }

        /// <inheritdoc />
        public override string EventName => nameof(RateLimited);
    }

    /// <summary>
    /// A provider spend cap hit. <paramref name="NoAllowedSourceRemains"/> is true only when rerouting
    /// (doc 05) found no other allowed source; otherwise the machine does not change state.
    /// </summary>
    public sealed record SpendCapped : AvailabilityEvent
    {
        /// <summary>Creates the event.</summary>
        public SpendCapped(DateTimeOffset scheduledAt, bool noAllowedSourceRemains, DateTimeOffset? resetAt) : base(scheduledAt)
        {
            NoAllowedSourceRemains = noAllowedSourceRemains;
            ResetAt = resetAt;
        }

        /// <summary>True when no allowed source is left for the agent.</summary>
        public bool NoAllowedSourceRemains { get; }

        /// <summary>When the cap resets, if the provider said.</summary>
        public DateTimeOffset? ResetAt { get; }

        /// <inheritdoc />
        public override string EventName => nameof(SpendCapped);
    }

    /// <summary>An owner interrupt tagged emergency. Bypasses vacation and never clears provider overlays.</summary>
    public sealed record OwnerEmergency : AvailabilityEvent
    {
        /// <summary>Creates the event.</summary>
        public OwnerEmergency(DateTimeOffset scheduledAt) : base(scheduledAt) { }

        /// <inheritdoc />
        public override string EventName => nameof(OwnerEmergency);
    }

    /// <summary>A cover agent starts answering for this agent (sets the Covered overlay).</summary>
    public sealed record CoverAssigned : AvailabilityEvent
    {
        /// <summary>Creates the event.</summary>
        public CoverAssigned(DateTimeOffset scheduledAt, string coverAgentId) : base(scheduledAt)
        {
            CoverAgentId = coverAgentId;
        }

        /// <summary>The covering agent.</summary>
        public string CoverAgentId { get; }

        /// <inheritdoc />
        public override string EventName => nameof(CoverAssigned);
    }

    /// <summary>The cover stops answering for this agent (clears the Covered overlay).</summary>
    public sealed record CoverReleased : AvailabilityEvent
    {
        /// <summary>Creates the event.</summary>
        public CoverReleased(DateTimeOffset scheduledAt) : base(scheduledAt) { }

        /// <inheritdoc />
        public override string EventName => nameof(CoverReleased);
    }
}

/// <summary>
/// Idempotency key of an applied event: (agent, event name, scheduled instant).
/// </summary>
/// <param name="AgentId">Agent the event applies to.</param>
/// <param name="EventName">Name of the event type.</param>
/// <param name="ScheduledAt">Instant the event is scheduled for.</param>
public sealed record AvailabilityIdempotencyKey(string AgentId, string EventName, DateTimeOffset ScheduledAt);

/// <summary>
/// Result of applying one event. <see cref="Applied"/> is false for duplicates, invalid transitions and
/// events that need no state change; the snapshot is then the unchanged input.
/// </summary>
/// <param name="Snapshot">Snapshot after the event.</param>
/// <param name="Applied">True when the event was accepted and the version increased.</param>
/// <param name="Reason">Why the event was accepted or rejected.</param>
public sealed record AvailabilityTransition(AvailabilitySnapshot Snapshot, bool Applied, string Reason);

/// <summary>
/// Pure availability state machine for one agent (doc 09). No I/O and no clock reads: every instant
/// comes from the caller.
/// </summary>
public static class AvailabilityRules
{
    /// <summary>
    /// Applies <paramref name="evt"/> to <paramref name="current"/>. Deterministic: the same inputs always give the same result.
    /// </summary>
    /// <param name="current">Snapshot before the event.</param>
    /// <param name="evt">The event.</param>
    /// <param name="agentId">Agent the machine belongs to; part of the idempotency key.</param>
    /// <param name="applied">Idempotency keys already applied for this agent.</param>
    /// <returns>The transition result. Does not mutate <paramref name="applied"/>.</returns>
    /// <exception cref="ArgumentException"><paramref name="agentId"/> is null, empty or whitespace.</exception>
    public static AvailabilityTransition Apply(
        AvailabilitySnapshot current,
        AvailabilityEvent evt,
        string agentId,
        IReadOnlySet<AvailabilityIdempotencyKey> applied)
    {
        ArgumentNullException.ThrowIfNull(current);
        ArgumentNullException.ThrowIfNull(evt);
        ArgumentNullException.ThrowIfNull(applied);
        ValidateAgentId(agentId);

        var key = new AvailabilityIdempotencyKey(agentId, evt.EventName, evt.ScheduledAt);
        if (applied.Contains(key))
        {
            return new AvailabilityTransition(current, false, "duplicate: already applied");
        }

        var state = current.State;
        switch (evt)
        {
            case AvailabilityEvent.ShiftEnd or AvailabilityEvent.BreakDue
                or AvailabilityEvent.VacationStarts or AvailabilityEvent.TokenBreak:
                return state == AvailabilityMachineState.OnShift
                    ? Accept(current, AvailabilityMachineState.Draining, current.Overlays, $"{evt.EventName}: draining", current.ProviderResetAt)
                    : Reject(current, $"{evt.EventName} is not valid in {state}");

            case AvailabilityEvent.DrainComplete drain:
                if (state != AvailabilityMachineState.Draining)
                {
                    return Reject(current, $"DrainComplete is not valid in {state}");
                }

                return drain.Target is AvailabilityMachineState.Break or AvailabilityMachineState.Off or AvailabilityMachineState.Vacation
                    ? Accept(current, drain.Target, current.Overlays, "drain complete", current.ProviderResetAt)
                    : Reject(current, $"DrainComplete cannot target {drain.Target}");

            case AvailabilityEvent.ScheduleStart or AvailabilityEvent.BreakEnd:
                return state is AvailabilityMachineState.Break or AvailabilityMachineState.Off
                    ? Accept(current, AvailabilityMachineState.OnShift, current.Overlays, $"{evt.EventName}: on shift", current.ProviderResetAt)
                    : Reject(current, $"{evt.EventName} is not valid in {state}");

            case AvailabilityEvent.RateLimited limited:
                return Accept(
                    current,
                    state,
                    WithOverlay(current.Overlays, new AvailabilityOverlay.Throttled(limited.Until), o => o is AvailabilityOverlay.Throttled),
                    "rate limited",
                    current.ProviderResetAt);

            case AvailabilityEvent.SpendCapped capped:
                // Rerouting to another allowed source is the caller's job (doc 05); no state change here.
                if (!capped.NoAllowedSourceRemains)
                {
                    return new AvailabilityTransition(current, false, "spend cap: an allowed source remains, reroute, no state change");
                }

                return Accept(current, AvailabilityMachineState.Vacation, current.Overlays, "provider cap", capped.ResetAt);

            case AvailabilityEvent.OwnerEmergency:
                // Bypasses vacation. Provider overlays and the provider reset time are kept.
                return Accept(current, AvailabilityMachineState.OnShift, current.Overlays, "owner emergency", current.ProviderResetAt);

            case AvailabilityEvent.CoverAssigned assigned:
                if (string.IsNullOrWhiteSpace(assigned.CoverAgentId))
                {
                    return Reject(current, "CoverAssigned needs a cover agent id");
                }

                return Accept(
                    current,
                    state,
                    WithOverlay(current.Overlays, new AvailabilityOverlay.Covered(assigned.CoverAgentId), o => o is AvailabilityOverlay.Covered),
                    "covered",
                    current.ProviderResetAt);

            case AvailabilityEvent.CoverReleased:
                if (!current.Overlays.Any(o => o is AvailabilityOverlay.Covered))
                {
                    return Reject(current, "CoverReleased: no cover is assigned");
                }

                return Accept(
                    current,
                    state,
                    current.Overlays.Where(o => o is not AvailabilityOverlay.Covered).ToList(),
                    "cover released",
                    current.ProviderResetAt);

            default:
                return Reject(current, $"unknown event {evt.EventName}");
        }
    }

    /// <summary>Validates an agent id for the machine. Null, empty and whitespace ids are programmer errors.</summary>
    /// <param name="agentId">Agent id to check.</param>
    /// <exception cref="ArgumentException"><paramref name="agentId"/> is null, empty or whitespace.</exception>
    internal static void ValidateAgentId(string agentId)
    {
        if (string.IsNullOrWhiteSpace(agentId))
        {
            throw new ArgumentException("Agent id must be a non-empty value.", nameof(agentId));
        }
    }

    private static AvailabilityTransition Accept(
        AvailabilitySnapshot current,
        AvailabilityMachineState state,
        IReadOnlyList<AvailabilityOverlay> overlays,
        string reason,
        DateTimeOffset? providerResetAt)
    {
        var next = new AvailabilitySnapshot(state, overlays, current.Version + 1, reason, providerResetAt);
        return new AvailabilityTransition(next, true, reason);
    }

    private static AvailabilityTransition Reject(AvailabilitySnapshot current, string reason) =>
        new(current, false, reason);

    private static List<AvailabilityOverlay> WithOverlay(
        IReadOnlyList<AvailabilityOverlay> overlays,
        AvailabilityOverlay overlay,
        Func<AvailabilityOverlay, bool> replaces)
    {
        var kept = overlays.Where(o => !replaces(o)).ToList();
        kept.Add(overlay);
        return kept;
    }
}

/// <summary>
/// One agent's availability machine. Holds the current snapshot and the idempotency keys it has applied.
/// Single writer: callers serialise calls per agent.
/// </summary>
public sealed class AvailabilityMachine
{
    private readonly HashSet<AvailabilityIdempotencyKey> _applied = [];

    /// <summary>Creates a machine at version 0.</summary>
    /// <param name="agentId">Agent the machine belongs to.</param>
    /// <param name="initialState">State the agent starts in.</param>
    /// <exception cref="ArgumentException"><paramref name="agentId"/> is null, empty or whitespace.</exception>
    public AvailabilityMachine(string agentId, AvailabilityMachineState initialState)
    {
        AvailabilityRules.ValidateAgentId(agentId);
        AgentId = agentId;
        Snapshot = new AvailabilitySnapshot(initialState, [], 0, "initial");
    }

    /// <summary>Agent this machine belongs to.</summary>
    public string AgentId { get; }

    /// <summary>Current snapshot.</summary>
    public AvailabilitySnapshot Snapshot { get; private set; }

    /// <summary>
    /// Applies an event and, when accepted, makes its snapshot current and records its idempotency key.
    /// </summary>
    /// <param name="evt">The event.</param>
    /// <returns>The transition result.</returns>
    public AvailabilityTransition Submit(AvailabilityEvent evt)
    {
        var transition = AvailabilityRules.Apply(Snapshot, evt, AgentId, _applied);
        if (transition.Applied)
        {
            _applied.Add(new AvailabilityIdempotencyKey(AgentId, evt.EventName, evt.ScheduledAt));
            Snapshot = transition.Snapshot;
        }

        return transition;
    }
}
