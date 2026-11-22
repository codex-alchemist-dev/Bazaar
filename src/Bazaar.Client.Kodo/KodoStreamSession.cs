using Bazaar.Client.Kodo.Live;
using Bazaar.Contracts.Kodo;

namespace Bazaar.Client.Kodo;

/// <summary>
/// What the panes need around the one stream tracker (doc 46 sections 14.6 and 15.2): it owns a <see cref="KodoStreamTracker"/>
/// and the client clock, and adds the parts the tracker leaves to its caller. These are the "snapshot needed" flag, the
/// <see cref="Changed"/> and <see cref="SnapshotRequested"/> events, the time of the last received frame, and the last known
/// score. The tracker still decides every state; this class never derives one. After a gap or a reconnect the display is
/// <c>unobserved</c> until a snapshot arrives, so a stale green is never shown. Not thread-safe: call it from one context.
/// </summary>
public sealed class KodoStreamSession(TimeProvider time, KodoObservationBudgets? budgets = null)
{
    private readonly TimeProvider _time = time ?? throw new ArgumentNullException(nameof(time));
    private readonly KodoStreamTracker _tracker = new(budgets);

    /// <summary>Raised after every frame or reconnect that reached the session, whatever the result, so a view can refresh.</summary>
    public event EventHandler? Changed;

    /// <summary>Raised once when a gap or a reconnect makes a fresh snapshot necessary. The owner re-subscribes or re-fetches.</summary>
    public event EventHandler? SnapshotRequested;

    /// <summary>True until a snapshot has been applied, and again after a gap or a reconnect.</summary>
    public bool NeedsSnapshot { get; private set; } = true;

    /// <summary>The revision of the last frame applied, or 0 before any snapshot.</summary>
    public long Revision => _tracker.Revision;

    /// <summary>When the client last received a readable frame, on its own clock, or null.</summary>
    public DateTimeOffset? LastEventAt { get; private set; }

    /// <summary>The score of the last snapshot applied, or null before one. Shown only as "last known" while offline.</summary>
    public int? LastKnownScore { get; private set; }

    /// <summary>The journal head of the last snapshot applied, or null.</summary>
    public string? JournalHead { get; private set; }

    /// <summary>What to show now. <c>unobserved</c> while a snapshot is needed, or when the tracker says so.</summary>
    public KodoMonitorState Current()
    {
        var current = _tracker.Current(_time.GetUtcNow());
        return NeedsSnapshot ? new KodoMonitorState("unobserved", null, null, []) : current;
    }

    /// <summary>How long the stream has been silent, never negative, or null when no frame was ever received.</summary>
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

    /// <summary>Applies one stream frame received now.</summary>
    public KodoApplyResult Apply(KodoFrameWire frame)
    {
        ArgumentNullException.ThrowIfNull(frame);

        var result = frame.Type == "snapshot" ? ApplySnapshotCore(frame.Snapshot) : ApplyDelta(frame);
        Changed?.Invoke(this, EventArgs.Empty);
        return result;
    }

    /// <summary>Applies a snapshot read from the read API, not the stream.</summary>
    public KodoApplyResult ApplySnapshot(KodoSnapshotWire? snapshot)
    {
        var result = ApplySnapshotCore(snapshot);
        Changed?.Invoke(this, EventArgs.Empty);
        return result;
    }

    private KodoApplyResult ApplySnapshotCore(KodoSnapshotWire? snapshot)
    {
        var now = _time.GetUtcNow();

        // A snapshot older than what we hold, while in sync, is not applied: revisions never go backwards.
        if (!NeedsSnapshot && snapshot is not null && snapshot.Revision < _tracker.Revision)
        {
            LastEventAt = now;
            return new KodoApplyResult(KodoApplyOutcome.Duplicate);
        }

        var result = _tracker.ApplySnapshot(snapshot, now);
        if (result.Outcome == KodoApplyOutcome.Applied)
        {
            LastEventAt = now;
            LastKnownScore = snapshot!.Score;
            JournalHead = snapshot.JournalHead;
            NeedsSnapshot = false;
        }

        return result;
    }

    private KodoApplyResult ApplyDelta(KodoFrameWire frame)
    {
        var now = _time.GetUtcNow();

        // After a gap the tracker's revision is stale, so it must not take deltas until a snapshot arrives.
        var result = NeedsSnapshot && frame.Type is "state_changed" or "heartbeat"
            ? new KodoApplyResult(KodoApplyOutcome.NeedsSnapshot, "A snapshot is needed first.")
            : _tracker.Apply(frame, now);

        if (result.Outcome != KodoApplyOutcome.Rejected)
        {
            LastEventAt = now;
        }

        if (result.Outcome == KodoApplyOutcome.NeedsSnapshot)
        {
            RequireSnapshot();
        }

        return result;
    }

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
