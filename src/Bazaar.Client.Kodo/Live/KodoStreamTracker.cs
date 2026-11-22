namespace Bazaar.Client.Kodo.Live;

/// <summary>What the tracker did with a frame.</summary>
public enum KodoApplyOutcome
{
    /// <summary>The frame advanced the view.</summary>
    Applied,

    /// <summary>The frame was already seen (same or older revision). Liveness is recorded; nothing else changes.</summary>
    Duplicate,

    /// <summary>A revision was skipped or no snapshot is held yet. The caller must re-fetch the snapshot (doc 46 section 15.2).</summary>
    NeedsSnapshot,

    /// <summary>The frame could not be trusted (unknown state, wrong schema, missing part). The view is unchanged.</summary>
    Rejected,
}

/// <summary>The result of applying one frame.</summary>
public sealed record KodoApplyResult(KodoApplyOutcome Outcome, string? Reason = null);

/// <summary>The two budgets that decide <c>unobserved</c>, both on the client's own clock (doc 46 section 8.3, PROPOSED in 15.6).</summary>
public sealed record KodoObservationBudgets(TimeSpan Heartbeat, TimeSpan Freshness)
{
    /// <summary>The shipped budgets: 30 s of silence, 60 s since the last snapshot.</summary>
    public static KodoObservationBudgets Defaults { get; } = new(TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(60));
}

/// <summary>One component to show.</summary>
public sealed record KodoComponentView(string Id, string State, string? Cause, DateTimeOffset Since);

/// <summary>What the Monitor shows right now. <see cref="State"/> is one of the six display words, <c>unobserved</c> included.</summary>
public sealed record KodoMonitorState(
    string State,
    string? Underlying,
    int? Score,
    IReadOnlyList<KodoComponentView> Components);

/// <summary>
/// Follows the <c>kodo</c> stream for one client (doc 46 sections 8.3 and 15.2). It applies snapshots and state changes in
/// revision order, asks for a new snapshot on any gap, and derives <c>unobserved</c> from the times it received things, never
/// from the server's clock. It decides no health state: it shows what the server sent, or <c>unobserved</c>. Not thread-safe;
/// the caller serialises frames and reads.
/// </summary>
public sealed class KodoStreamTracker(KodoObservationBudgets? budgets = null)
{
    private static readonly HashSet<string> _states = new(["healthy", "degraded", "failing", "recovering", "frozen"], StringComparer.Ordinal);
    private static readonly HashSet<string> _probeStates = new(["green", "amber", "red", "unknown"], StringComparer.Ordinal);

    private readonly KodoObservationBudgets _budgets = budgets ?? KodoObservationBudgets.Defaults;

    private KodoSnapshotWire? _snapshot;
    private string? _state;
    private string? _underlying;
    private long _revision;
    private DateTimeOffset? _lastFrameAt;
    private DateTimeOffset? _lastSnapshotAt;

    /// <summary>The revision of the last frame applied, or 0 before any snapshot.</summary>
    public long Revision => _revision;

    /// <summary>Applies one frame received at <paramref name="receivedAt"/> on the client clock.</summary>
    public KodoApplyResult Apply(KodoFrameWire frame, DateTimeOffset receivedAt)
    {
        ArgumentNullException.ThrowIfNull(frame);

        switch (frame.Type)
        {
            case "snapshot":
                return ApplySnapshot(frame.Snapshot, receivedAt);
            case "state_changed":
                return ApplyDelta(frame, receivedAt, change: true);
            case "heartbeat":
                return ApplyDelta(frame, receivedAt, change: false);
            default:
                return new KodoApplyResult(KodoApplyOutcome.Rejected, $"Unknown Kodo frame type '{frame.Type}'.");
        }
    }

    /// <summary>Applies a snapshot read from the read API, not the stream.</summary>
    public KodoApplyResult ApplySnapshot(KodoSnapshotWire? snapshot, DateTimeOffset receivedAt)
    {
        if (snapshot is null)
        {
            return new KodoApplyResult(KodoApplyOutcome.Rejected, "A snapshot frame carried no snapshot.");
        }

        if (snapshot.SchemaVersion != 1)
        {
            return new KodoApplyResult(KodoApplyOutcome.Rejected, $"Unsupported Kodo schema version {snapshot.SchemaVersion}.");
        }

        if (!_states.Contains(snapshot.State) || !_states.Contains(snapshot.Underlying))
        {
            return new KodoApplyResult(KodoApplyOutcome.Rejected, $"Unknown Kodo state '{snapshot.State}'.");
        }

        if (snapshot.Components.Any(c => !_probeStates.Contains(c.State)))
        {
            return new KodoApplyResult(KodoApplyOutcome.Rejected, "A component has an unknown status.");
        }

        _snapshot = snapshot;
        _state = snapshot.State;
        _underlying = snapshot.Underlying;
        _revision = snapshot.Revision;
        _lastFrameAt = receivedAt;
        _lastSnapshotAt = receivedAt;
        return new KodoApplyResult(KodoApplyOutcome.Applied);
    }

    private KodoApplyResult ApplyDelta(KodoFrameWire frame, DateTimeOffset receivedAt, bool change)
    {
        if (_snapshot is null)
        {
            return new KodoApplyResult(KodoApplyOutcome.NeedsSnapshot, "No snapshot yet.");
        }

        if (frame.Revision <= _revision)
        {
            _lastFrameAt = receivedAt;
            return new KodoApplyResult(KodoApplyOutcome.Duplicate);
        }

        if (frame.Revision != _revision + 1)
        {
            return new KodoApplyResult(KodoApplyOutcome.NeedsSnapshot, $"Revision {frame.Revision} follows {_revision}.");
        }

        if (change)
        {
            KodoStateChangeWire? delta = frame.StateChange;
            if (delta is null || !_states.Contains(delta.State) || !_states.Contains(delta.Underlying))
            {
                return new KodoApplyResult(KodoApplyOutcome.Rejected, "A state change was missing or named an unknown state.");
            }

            _state = delta.State;
            _underlying = delta.Underlying;
        }

        _revision = frame.Revision;
        _lastFrameAt = receivedAt;
        return new KodoApplyResult(KodoApplyOutcome.Applied);
    }

    /// <summary>What to show at <paramref name="now"/>. Never throws; with nothing received it is <c>unobserved</c>.</summary>
    public KodoMonitorState Current(DateTimeOffset now)
    {
        if (_snapshot is null || _state is null || _lastFrameAt is null || _lastSnapshotAt is null
            || Age(now, _lastFrameAt.Value) > _budgets.Heartbeat
            || Age(now, _lastSnapshotAt.Value) > _budgets.Freshness)
        {
            return new KodoMonitorState("unobserved", null, null, []);
        }

        return new KodoMonitorState(
            _state,
            _state == "frozen" ? _underlying : null,
            _snapshot.Score,
            _snapshot.Components.Select(c => new KodoComponentView(c.Id, c.State, c.Cause, c.Since)).ToList());
    }

    private static TimeSpan Age(DateTimeOffset now, DateTimeOffset at) => now < at ? TimeSpan.Zero : now - at;
}
