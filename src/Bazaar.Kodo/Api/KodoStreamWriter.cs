using Bazaar.Kodo.Evaluator;

namespace Bazaar.Kodo.Api;

/// <summary>
/// Numbers the events of one <c>kodo</c> stream (doc 46 section 15.2). Every event takes the next revision under a lock, so the
/// numbers are unique and gap-free even when several threads publish. A snapshot must carry the revision it is sent with, so a
/// client can apply the deltas after it.
/// </summary>
/// <remarks>
/// The writer holds the counter only. It is not persisted: a restarted Kodo starts from the revision the caller passes in, and
/// clients treat a lower revision than they hold as a reset and re-fetch the snapshot (PROPOSED, doc 46 section 15.6).
/// </remarks>
public sealed class KodoStreamWriter
{
    private readonly Lock _gate = new();
    private long _lastRevision;

    /// <summary>Starts a stream whose next event is <paramref name="lastRevision"/> plus one.</summary>
    /// <param name="lastRevision">The revision already issued on this stream. Zero for a new stream.</param>
    public KodoStreamWriter(long lastRevision = 0)
    {
        if (lastRevision < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(lastRevision), lastRevision, "The revision must not be negative.");
        }

        _lastRevision = lastRevision;
    }

    /// <summary>The revision of the last event issued, or the starting value when none was.</summary>
    public long LastRevision
    {
        get
        {
            lock (_gate)
            {
                return _lastRevision;
            }
        }
    }

    /// <summary>The revision the next event will carry. A snapshot built for the next event passes this value to the builder.</summary>
    public long NextRevision
    {
        get
        {
            lock (_gate)
            {
                return NextRevisionLocked();
            }
        }
    }

    /// <summary>
    /// A snapshot event. Its <see cref="KodoStatusSnapshot.Revision"/> must be the revision the event takes, which is
    /// <see cref="NextRevision"/> when it is called.
    /// </summary>
    /// <exception cref="ArgumentException">The snapshot's revision is not the next revision.</exception>
    public KodoStreamEvent Snapshot(KodoStatusSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);

        lock (_gate)
        {
            long revision = NextRevisionLocked();
            if (snapshot.Revision != revision)
            {
                throw new ArgumentException(
                    $"The snapshot reflects revision {snapshot.Revision}, but the next event is revision {revision}.",
                    nameof(snapshot));
            }

            _lastRevision = revision;
            return new KodoStreamEvent(revision, KodoStreamEvent.SnapshotType, snapshot.GeneratedAt, null, snapshot);
        }
    }

    /// <summary>
    /// A state-change event. The displayed state must differ from <paramref name="previous"/>. A change of the underlying
    /// state while the displayed state stays frozen is not a state change, so no event is written for it.
    /// </summary>
    /// <param name="previous">The displayed state before the change.</param>
    /// <param name="evaluation">The evaluator's output after the change.</param>
    /// <param name="at">When the change happened, in UTC.</param>
    /// <exception cref="ArgumentException">The displayed state did not change.</exception>
    public KodoStreamEvent StateChanged(KodoState previous, KodoEvaluation evaluation, DateTimeOffset at)
    {
        ArgumentNullException.ThrowIfNull(evaluation);

        if (previous == evaluation.State)
        {
            throw new ArgumentException("A state-changed event needs a different displayed state.", nameof(evaluation));
        }

        KodoStateChange change = new(
            KodoWireNames.State(previous),
            KodoWireNames.State(evaluation.State),
            KodoWireNames.State(evaluation.Underlying));

        return Next(KodoStreamEvent.StateChangedType, at, change, null);
    }

    /// <summary>A heartbeat event. It only proves liveness and carries no payload.</summary>
    public KodoStreamEvent Heartbeat(DateTimeOffset at) => Next(KodoStreamEvent.HeartbeatType, at, null, null);

    private KodoStreamEvent Next(string type, DateTimeOffset at, KodoStateChange? change, KodoStatusSnapshot? snapshot)
    {
        lock (_gate)
        {
            long revision = NextRevisionLocked();
            _lastRevision = revision;
            return new KodoStreamEvent(revision, type, at, change, snapshot);
        }
    }

    private long NextRevisionLocked()
    {
        if (_lastRevision == long.MaxValue)
        {
            throw new InvalidOperationException("The kodo stream has no revisions left. Start a new stream.");
        }

        return _lastRevision + 1;
    }
}
