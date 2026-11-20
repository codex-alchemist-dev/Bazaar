using Bazaar.Contracts.Kodo;
using Bazaar.Kodo.Evaluator;

namespace Bazaar.Kodo.Api;

/// <summary>
/// Numbers the events of one <c>kodo</c> stream (doc 46 section 15.2). Every delta (a state change or a heartbeat) takes the next
/// revision under a lock, so the numbers are unique and gap-free even when several threads publish. A snapshot is not a delta:
/// it is stamped with the current revision under the same lock and does not raise it, so a snapshot sent to one subscriber on
/// connect leaves no gap for the others.
/// </summary>
/// <remarks>
/// The writer holds the counter only. It is not persisted: a restarted Kodo starts from the revision the caller passes in, and
/// clients treat a lower revision than they hold as a reset and re-fetch the snapshot (PROPOSED, doc 46 section 15.6).
/// </remarks>
public sealed class KodoStreamWriter
{
    private readonly Lock _gate = new();
    private long _lastRevision;

    /// <summary>Starts a stream whose next delta is <paramref name="lastRevision"/> plus one.</summary>
    /// <param name="lastRevision">The revision already issued on this stream. Zero for a new stream.</param>
    public KodoStreamWriter(long lastRevision = 0)
    {
        if (lastRevision < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(lastRevision), lastRevision, "The revision must not be negative.");
        }

        _lastRevision = lastRevision;
    }

    /// <summary>The revision of the last delta issued, or the starting value when none was. A snapshot taken now carries it.</summary>
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

    /// <summary>
    /// A snapshot event for one subscriber. The writer calls <paramref name="build"/> under its lock with the current revision, so
    /// the snapshot reflects exactly that revision however many threads publish at the same moment. It never raises the revision
    /// and never fails because of a concurrent publish.
    /// </summary>
    /// <param name="build">Builds the snapshot for the revision it is given, typically with <see cref="KodoStatusSnapshotBuilder"/>.
    /// It runs under the lock, so it must be quick and must not call this writer.</param>
    /// <param name="at">When the event was made, in UTC.</param>
    /// <exception cref="InvalidOperationException"><paramref name="build"/> returned a snapshot with a different revision.</exception>
    public KodoStreamEventDto Snapshot(Func<long, KodoStatusSnapshotDto> build, DateTimeOffset at)
    {
        ArgumentNullException.ThrowIfNull(build);

        lock (_gate)
        {
            long revision = _lastRevision;
            KodoStatusSnapshotDto snapshot = build(revision)
                ?? throw new InvalidOperationException("The snapshot builder returned null.");
            if (snapshot.Revision != revision)
            {
                throw new InvalidOperationException(
                    $"The snapshot builder returned revision {snapshot.Revision}, but the stream is at revision {revision}.");
            }

            return new KodoStreamEventDto(revision, KodoStreamEventDto.SnapshotType, at, null, snapshot);
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
    public KodoStreamEventDto StateChanged(KodoState previous, KodoEvaluation evaluation, DateTimeOffset at)
    {
        ArgumentNullException.ThrowIfNull(evaluation);

        if (previous == evaluation.State)
        {
            throw new ArgumentException("A state-changed event needs a different displayed state.", nameof(evaluation));
        }

        KodoStateChangeDto change = new(
            KodoWireNames.State(previous),
            KodoWireNames.State(evaluation.State),
            evaluation.State == KodoState.Frozen ? KodoWireNames.State(evaluation.Underlying) : null);

        return Next(KodoStreamEventDto.StateChangedType, at, change);
    }

    /// <summary>A heartbeat event. It only proves liveness and carries no payload.</summary>
    public KodoStreamEventDto Heartbeat(DateTimeOffset at) => Next(KodoStreamEventDto.HeartbeatType, at, null);

    private KodoStreamEventDto Next(string type, DateTimeOffset at, KodoStateChangeDto? change)
    {
        lock (_gate)
        {
            if (_lastRevision == long.MaxValue)
            {
                throw new InvalidOperationException("The kodo stream has no revisions left. Start a new stream.");
            }

            long revision = _lastRevision + 1;
            _lastRevision = revision;
            return new KodoStreamEventDto(revision, type, at, change, null);
        }
    }
}
