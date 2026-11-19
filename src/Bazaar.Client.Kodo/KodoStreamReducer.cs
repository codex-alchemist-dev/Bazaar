using Bazaar.Contracts.Kodo;

namespace Bazaar.Client.Kodo;

/// <summary>What the reducer did with one stream event.</summary>
public enum KodoReduceResult
{
    /// <summary>The event was applied in order.</summary>
    Applied,

    /// <summary>The revision was already seen. Nothing changed, but the event still proves the stream is alive.</summary>
    Duplicate,

    /// <summary>A revision was skipped or a state change did not follow the known state. The reducer now needs a fresh snapshot.</summary>
    GapDetected,

    /// <summary>A delta arrived while a snapshot is still needed. It was not applied.</summary>
    AwaitingSnapshot,

    /// <summary>The event could not be read (unknown type, state or schema, or a missing payload). It was not applied.</summary>
    Rejected,
}

/// <summary>
/// Client side of the <c>kodo</c> stream (doc 46 sections 8.3, 14.1 and 15.2): applies a snapshot, then deltas by revision, and
/// derives <c>unobserved</c> from the time the last event was received. Out-of-order or skipped deltas are never applied:
/// a gap sets <see cref="NeedsSnapshot"/> and the display turns <c>unobserved</c> until a snapshot arrives, so a stale
/// green can never be shown. Not thread-safe: the owner calls it from one context (the UI thread or a single reader).
/// </summary>
public sealed class KodoStreamReducer(TimeProvider time, KodoUnobservedBudgets? budgets = null)
{
    private readonly TimeProvider _time = time ?? throw new ArgumentNullException(nameof(time));
    private readonly KodoUnobservedBudgets _budgets = budgets ?? KodoUnobservedBudgets.Defaults;

    private KodoStatusSnapshotDto? _snapshot;
    private KodoStatusDto? _status;
    private KodoDisplayState? _lastKnown;

    /// <summary>Raised after every call that reached the reducer, whatever the result, so a view can refresh.</summary>
    public event EventHandler? Changed;

    /// <summary>Raised once when a gap or a reconnect makes a fresh snapshot necessary. The owner re-subscribes or re-fetches.</summary>
    public event EventHandler? SnapshotRequested;

    /// <summary>True until a snapshot has been applied, and again after a gap or a reconnect.</summary>
    public bool NeedsSnapshot { get; private set; } = true;

    /// <summary>The revision the reducer reflects, or null before the first snapshot.</summary>
    public long? Revision { get; private set; }

    /// <summary>When the client last received any event on the stream, on its own clock, or null.</summary>
    public DateTimeOffset? LastEventAt { get; private set; }

    /// <summary>The status as of the last snapshot with the latest state change applied, or null before a snapshot.</summary>
    public KodoStatusDto? Status => _status;

    /// <summary>The components of the last snapshot. Empty before one.</summary>
    public IReadOnlyList<KodoComponentDto> Components => _snapshot?.Components ?? [];

    /// <summary>The journal head of the last snapshot, or null.</summary>
    public string? JournalHead => _snapshot?.JournalHead;

    /// <summary>
    /// The value to show now. <c>Unobserved</c> when no state is known, a snapshot is needed, or the stream was silent past
    /// the budget (<see cref="KodoUnobserved.Derive"/>).
    /// </summary>
    public KodoDisplayState Display()
    {
        var known = NeedsSnapshot ? null : _lastKnown;
        return KodoUnobserved.Derive(known, _time.GetUtcNow(), LastEventAt, _budgets);
    }

    /// <summary>How long the stream has been silent, never negative, or null when no event was ever received.</summary>
    public TimeSpan? SilentFor()
    {
        if (LastEventAt is not { } last)
        {
            return null;
        }

        var age = _time.GetUtcNow() - last;
        return age < TimeSpan.Zero ? TimeSpan.Zero : age;
    }

    /// <summary>The connection dropped and came back. The next snapshot is needed before any delta is trusted.</summary>
    public void Reconnected()
    {
        RequireSnapshot();
        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Applies one stream event.</summary>
    public KodoReduceResult Apply(KodoStreamEventDto streamEvent)
    {
        ArgumentNullException.ThrowIfNull(streamEvent);

        var result = ApplyCore(streamEvent);
        Changed?.Invoke(this, EventArgs.Empty);
        return result;
    }

    private KodoReduceResult ApplyCore(KodoStreamEventDto e)
    {
        switch (e.Type)
        {
            case KodoStreamEventDto.SnapshotType:
                return ApplySnapshot(e);
            case KodoStreamEventDto.StateChangedType:
            case KodoStreamEventDto.HeartbeatType:
                return ApplyDelta(e);
            default:
                return KodoReduceResult.Rejected;
        }
    }

    private KodoReduceResult ApplySnapshot(KodoStreamEventDto e)
    {
        if (e.Snapshot is not { } snapshot
            || snapshot.SchemaVersion != KodoStatusSnapshotDto.CurrentSchemaVersion
            || !KodoUnobserved.TryDisplayOf(snapshot.Status.State, out var display))
        {
            return KodoReduceResult.Rejected;
        }

        MarkReceived();

        // A snapshot older than what we hold, while in sync, is not applied: revisions never go backwards.
        if (!NeedsSnapshot && Revision is { } current && snapshot.Revision < current)
        {
            return KodoReduceResult.Duplicate;
        }

        _snapshot = snapshot;
        _status = snapshot.Status;
        _lastKnown = display;
        Revision = snapshot.Revision;
        NeedsSnapshot = false;
        return KodoReduceResult.Applied;
    }

    private KodoReduceResult ApplyDelta(KodoStreamEventDto e)
    {
        if (e.Type == KodoStreamEventDto.StateChangedType
            && (e.StateChange is not { } incoming || !KodoUnobserved.TryDisplayOf(incoming.State, out _)))
        {
            return KodoReduceResult.Rejected;
        }

        MarkReceived();

        if (NeedsSnapshot || Revision is not { } revision || _status is null)
        {
            return KodoReduceResult.AwaitingSnapshot;
        }

        if (e.Revision <= revision)
        {
            return KodoReduceResult.Duplicate;
        }

        if (e.Revision != revision + 1)
        {
            RequireSnapshot();
            return KodoReduceResult.GapDetected;
        }

        if (e.Type == KodoStreamEventDto.StateChangedType)
        {
            var change = e.StateChange!;
            if (!KodoUnobserved.TryDisplayOf(change.Previous, out var previous) || previous != _lastKnown)
            {
                // The delta does not follow the state we hold: something was lost. Never guess.
                RequireSnapshot();
                return KodoReduceResult.GapDetected;
            }

            KodoUnobserved.TryDisplayOf(change.State, out var next);
            _lastKnown = next;
            _status = _status with { State = change.State, UnderlyingState = change.UnderlyingState };
        }

        Revision = e.Revision;
        return KodoReduceResult.Applied;
    }

    private void MarkReceived() => LastEventAt = _time.GetUtcNow();

    private void RequireSnapshot()
    {
        var already = NeedsSnapshot;
        NeedsSnapshot = true;
        if (!already)
        {
            SnapshotRequested?.Invoke(this, EventArgs.Empty);
        }
    }
}
