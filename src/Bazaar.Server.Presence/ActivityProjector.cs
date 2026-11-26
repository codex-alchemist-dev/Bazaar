using System.Collections.Concurrent;
using Bazaar.Server.Presence.Abstractions;
using Bazaar.Server.WorkLife;

namespace Bazaar.Server.Presence;

/// <summary>
/// The activity projector (docs/design/23, "Event sources and subscription"). One writer per agent: each agent has its own gate,
/// so events for one agent apply one at a time in arrival order while different agents run in parallel. An accepted event
/// folds into the agent's state, derives the label and overlays with <see cref="PresenceRules"/>, upserts the row with the next
/// revision and publishes the delta. An event older than the last applied source time, or equal to the last applied event, is dropped.
/// </summary>
/// <remarks>
/// Choices where the doc is silent:
/// <list type="bullet">
/// <item>An agent with no known availability starts as <see cref="PresenceAvailability.Off"/> until a transition or a restart seed says otherwise.</item>
/// <item>A publish that throws is swallowed: the row is already stored, and a client that misses the delta recovers with the snapshot.</item>
/// <item>
/// <c>unknown</c> (doc 23, "Restart"): a row shows <c>unknown</c> when no turn event has arrived for <see cref="PresenceOptions.UnknownAfter"/>
/// and either its activity is not idle (a turn that went quiet), or it was rebuilt at restart on shift with no turn event since (its
/// state cannot be confirmed). A rebuilt row that is off shift, on a break or on vacation is derived from availability alone and never goes unknown.
/// Any later turn event clears it. <see cref="SweepAsync"/> applies the rule; the hosted service calls it on a timer.
/// </item>
/// </list>
/// </remarks>
public sealed class ActivityProjector(
    IPresenceStore store,
    IPresencePublisher publisher,
    TimeProvider time,
    PresenceOptions options) : IActivityProjector
{
    private readonly ConcurrentDictionary<Guid, AgentState> _agents = new();

    /// <inheritdoc />
    public async ValueTask OnEventAsync(ActivityEvent e, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(e);
        var state = _agents.GetOrAdd(e.AgentId, id => new AgentState(id));
        await state.Gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await LoadRevisionAsync(state, ct).ConfigureAwait(false);
            if (e.SourceTime < state.LastSourceTime || e == state.LastEvent)
            {
                return;
            }

            state.Fold(e, time.GetUtcNow(), options);
            state.LastSourceTime = e.SourceTime;
            state.LastEvent = e;
            await WriteAsync(state, e.SourceTime, ct).ConfigureAwait(false);
        }
        finally
        {
            state.Gate.Release();
        }
    }

    /// <summary>
    /// Rebuilds rows from availability and open turns after a restart (doc 23). Each seed replaces whatever the projector held for
    /// that agent. The rebuilt rows carry no turn event time, so the <c>unknown</c> rule of <see cref="SweepAsync"/> applies to them.
    /// </summary>
    public async ValueTask RebuildAsync(IEnumerable<PresenceSeed> seeds, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(seeds);
        foreach (var seed in seeds)
        {
            var state = _agents.GetOrAdd(seed.AgentId, id => new AgentState(id));
            await state.Gate.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                await LoadRevisionAsync(state, ct).ConfigureAwait(false);
                var now = time.GetUtcNow();
                state.Reset(seed, now);
                if (seed.OpenTurn is { } turn)
                {
                    state.Fold(turn, now, options);
                    state.LastTurnEventAt = null;
                    state.LastSourceTime = turn.SourceTime;
                    state.LastEvent = turn;
                }

                await WriteAsync(state, now, ct).ConfigureAwait(false);
            }
            finally
            {
                state.Gate.Release();
            }
        }
    }

    /// <summary>Marks rows with no turn event for <see cref="PresenceOptions.UnknownAfter"/> as <c>unknown</c> (doc 23, "Restart"). Returns how many rows changed.</summary>
    public async ValueTask<int> SweepAsync(CancellationToken ct)
    {
        var changed = 0;
        foreach (var state in _agents.Values)
        {
            await state.Gate.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                var now = time.GetUtcNow();
                if (state.IsStale(now, options.UnknownAfter) && !state.Unknown)
                {
                    state.Unknown = true;
                    await WriteAsync(state, now, ct).ConfigureAwait(false);
                    changed++;
                }
            }
            finally
            {
                state.Gate.Release();
            }
        }

        return changed;
    }

    private async ValueTask LoadRevisionAsync(AgentState state, CancellationToken ct)
    {
        if (state.RevisionLoaded)
        {
            return;
        }

        // A restart starts with an empty store; the cache snapshot (when it has the row) keeps revisions climbing.
        var held = await store.GetAsync(state.AgentId, ct).ConfigureAwait(false);
        state.Revision = held?.Revision ?? 0;
        state.RevisionLoaded = true;
    }

    private async ValueTask WriteAsync(AgentState state, DateTimeOffset since, CancellationToken ct)
    {
        var now = time.GetUtcNow();
        var derived = state.Derive(options.IdleLabel);
        var row = new PresenceRow(
            state.AgentId,
            state.Revision + 1,
            state.Availability,
            derived.Overlays,
            derived.Label,
            state.Activity,
            state.GcId,
            state.TurnId,
            state.Label,
            state.SinceFor(derived.Label, state.Activity, since),
            now);
        if (!await store.UpsertAsync(row, ct).ConfigureAwait(false))
        {
            return;
        }

        state.Revision = row.Revision;
        state.Remember(row);
        try
        {
            await publisher.PublishDeltaAsync(row, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Best effort: the row is stored, and a client that missed the delta recovers with the snapshot.
        }
    }

    /// <summary>The folded state of one agent. Touched only while holding <see cref="Gate"/>.</summary>
    private sealed class AgentState(Guid agentId)
    {
        public Guid AgentId { get; } = agentId;

        public SemaphoreSlim Gate { get; } = new(1, 1);

        public bool RevisionLoaded { get; set; }

        public long Revision { get; set; }

        public DateTimeOffset LastSourceTime { get; set; } = DateTimeOffset.MinValue;

        public ActivityEvent? LastEvent { get; set; }

        public PresenceAvailability Availability { get; private set; } = PresenceAvailability.Off;

        public PresenceActivityKind Activity { get; private set; } = PresenceActivityKind.Idle;

        public PresenceToolClass ToolClass { get; private set; }

        public PresenceGcContext GcContext { get; private set; }

        public Guid? GcId { get; private set; }

        public Guid? TurnId { get; private set; }

        public string? Label { get; private set; }

        public bool Throttled { get; private set; }

        public bool Covered { get; private set; }

        public bool Ward { get; private set; }

        public bool WaitingForApproval { get; private set; }

        public bool Unknown { get; set; }

        public bool Rebuilt { get; private set; }

        public DateTimeOffset SeededAt { get; private set; }

        public DateTimeOffset? LastTurnEventAt { get; set; }

        private string? _lastPresence;
        private PresenceActivityKind _lastActivity;
        private DateTimeOffset _since;

        public void Reset(PresenceSeed seed, DateTimeOffset now)
        {
            Availability = seed.Availability;
            Throttled = seed.Throttled;
            Covered = seed.Covered;
            Activity = PresenceActivityKind.Idle;
            ToolClass = PresenceToolClass.None;
            GcContext = PresenceGcContext.Other;
            GcId = null;
            TurnId = null;
            Label = null;
            Ward = false;
            WaitingForApproval = false;
            Unknown = false;
            Rebuilt = true;
            SeededAt = now;
            LastTurnEventAt = null;
            LastSourceTime = DateTimeOffset.MinValue;
            LastEvent = null;
        }

        public void Fold(ActivityEvent e, DateTimeOffset now, PresenceOptions options)
        {
            switch (e.Kind)
            {
                case ActivityEventKind.AvailabilityChange:
                    Availability = e.Availability ?? Availability;
                    break;
                case ActivityEventKind.Turn:
                    Activity = e.Activity ?? PresenceActivityKind.Idle;
                    ToolClass = Activity == PresenceActivityKind.Tool ? e.ToolClass : PresenceToolClass.None;
                    GcContext = e.GcContext;
                    WaitingForApproval = false;
                    if (Activity == PresenceActivityKind.Idle)
                    {
                        GcId = null;
                        TurnId = null;
                        Label = null;
                    }
                    else
                    {
                        GcId = e.GcId;
                        TurnId = e.TurnId;
                        Label = Scrub(e.Label, options);
                    }

                    MarkTurnEvent(now);
                    break;
                case ActivityEventKind.QuotaThrottle:
                    Throttled = e.Active;
                    break;
                case ActivityEventKind.KanbanWait:
                    Wait(e.Active, approval: false, now);
                    break;
                case ActivityEventKind.ApprovalWait:
                    Wait(e.Active, approval: true, now);
                    break;
                case ActivityEventKind.Covered:
                    Covered = e.Active;
                    break;
                case ActivityEventKind.Ward:
                    Ward = e.Active;
                    break;
                default:
                    throw new ArgumentOutOfRangeException(nameof(e), e.Kind, "Unknown activity event kind.");
            }
        }

        public bool IsStale(DateTimeOffset now, TimeSpan after)
        {
            var quietSince = LastTurnEventAt ?? SeededAt;
            if (quietSince == default || now - quietSince < after)
            {
                return false;
            }

            if (Activity != PresenceActivityKind.Idle)
            {
                return true;
            }

            return Rebuilt && LastTurnEventAt is null && Availability is PresenceAvailability.OnShift or PresenceAvailability.Draining;
        }

        public PresenceResult Derive(string idleLabel)
        {
            var result = PresenceRules.Derive(
                new PresenceInput(Availability, Activity, ToolClass, GcContext, Throttled, Covered, WaitingForApproval),
                idleLabel);
            var overlays = Ward ? result.Overlays | PresenceOverlay.Ward : result.Overlays;
            return new PresenceResult(Unknown ? PresenceRow.UnknownLabel : result.Label, overlays);
        }

        public DateTimeOffset SinceFor(string presence, PresenceActivityKind activity, DateTimeOffset at) =>
            _lastPresence is null || presence != _lastPresence || activity != _lastActivity ? at : _since;

        public void Remember(PresenceRow row)
        {
            _lastPresence = row.Presence;
            _lastActivity = row.ActivityKind;
            _since = row.Since;
        }

        private void MarkTurnEvent(DateTimeOffset now)
        {
            LastTurnEventAt = now;
            Unknown = false;
        }

        private void Wait(bool active, bool approval, DateTimeOffset now)
        {
            if (active)
            {
                Activity = PresenceActivityKind.Waiting;
                ToolClass = PresenceToolClass.None;
                WaitingForApproval = approval;
            }
            else
            {
                WaitingForApproval = false;
                if (Activity == PresenceActivityKind.Waiting)
                {
                    Activity = PresenceActivityKind.Idle;
                }
            }

            MarkTurnEvent(now);
        }

        private static string? Scrub(string? label, PresenceOptions options)
        {
            if (options.ActivityLabelMode == ActivityLabelMode.KindsOnly || string.IsNullOrWhiteSpace(label))
            {
                return null;
            }

            // No control characters (a label is one line), then the 80 character cap of the column.
            var clean = string.Concat(label.Where(c => !char.IsControl(c))).Trim();
            if (clean.Length == 0)
            {
                return null;
            }

            return clean.Length <= PresenceRow.MaxLabelLength ? clean : clean[..PresenceRow.MaxLabelLength];
        }
    }
}
