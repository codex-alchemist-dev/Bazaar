using Bazaar.Server.Gc.Model;

namespace Bazaar.Server.Tests.Gc.Properties;

/// <summary>An event as the reference log stores it: the seq the log assigned and the draft it was built from.</summary>
/// <param name="Seq">Gap-free sequence number, starting at 1.</param>
/// <param name="Draft">The planner draft that was committed.</param>
public sealed record LoggedEvent(long Seq, GcEventDraft Draft);

/// <summary>Outcome of a commit attempt.</summary>
public enum CommitOutcome
{
    /// <summary>Every draft in the batch was appended.</summary>
    Committed,

    /// <summary>The batch was discarded. No row of it became visible.</summary>
    RolledBack,
}

/// <summary>
/// In-memory model of one GC's append-only event log, kept in the test folder only. Seq is head + 1,
/// gap-free from 1. Rows are never mutated or removed. A batch is staged and published all at once,
/// so a fault partway through leaves the head unchanged, which is the rollback the writer promises.
/// </summary>
public sealed class ReferenceGcLog
{
    private readonly List<LoggedEvent> _events = [];

    /// <summary>Highest assigned seq, or 0 for an empty log.</summary>
    public long Head => _events.Count;

    /// <summary>Every event in seq order. The list is a read-only view of the live log.</summary>
    public IReadOnlyList<LoggedEvent> All => _events.AsReadOnly();

    /// <summary>
    /// Appends a batch at head + 1 onward. When <paramref name="faultAtIndex"/> names a draft, the writer
    /// "fails" before that draft is staged; the staged rows are discarded and the head does not move.
    /// </summary>
    public CommitOutcome Commit(IReadOnlyList<GcEventDraft> drafts, int? faultAtIndex = null)
    {
        ArgumentNullException.ThrowIfNull(drafts);

        var staged = new List<LoggedEvent>(drafts.Count);
        var firstSeq = Head + 1;
        for (var i = 0; i < drafts.Count; i++)
        {
            if (faultAtIndex == i)
            {
                return CommitOutcome.RolledBack;
            }

            staged.Add(new LoggedEvent(firstSeq + i, drafts[i]));
        }

        _events.AddRange(staged);
        return CommitOutcome.Committed;
    }

    /// <summary>Up to <paramref name="limit"/> events with seq greater than <paramref name="afterSeq"/>, in seq order.</summary>
    public IReadOnlyList<LoggedEvent> Read(long afterSeq, int limit)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(afterSeq);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(limit);

        return _events.Where(e => e.Seq > afterSeq).Take(limit).ToList();
    }
}
