using Bazaar.Server.GeneticMemory.Abstractions;

namespace Bazaar.Server.GeneticMemory;

/// <summary>
/// Holds the lessons a child recorded with <c>gmt.propose_lesson</c> until its end hook reads them. In memory: a child's
/// proposals live only as long as its turn, so a restart loses an unfinished child's lessons, which is the safe direction.
/// Entries that are never read (a parent turn that called the tool) are bounded by <see cref="MaxTurns"/>, oldest dropped first.
/// </summary>
public sealed class GmtProposalStore
{
    /// <summary>Most turns held at once.</summary>
    public const int MaxTurns = 1000;

    private readonly Lock _gate = new();
    private readonly Dictionary<Guid, List<GmtProposal>> _byTurn = [];
    private readonly Queue<Guid> _order = new();

    /// <summary>Adds a proposal. Returns false when the turn already holds <paramref name="maxPerTurn"/>.</summary>
    public bool TryAdd(Guid turnId, GmtProposal proposal, int maxPerTurn)
    {
        ArgumentNullException.ThrowIfNull(proposal);
        lock (_gate)
        {
            if (!_byTurn.TryGetValue(turnId, out var list))
            {
                while (_byTurn.Count >= MaxTurns && _order.TryDequeue(out var oldest))
                {
                    _byTurn.Remove(oldest);
                }

                list = [];
                _byTurn[turnId] = list;
                _order.Enqueue(turnId);
            }

            if (list.Count >= maxPerTurn)
            {
                return false;
            }

            list.Add(proposal);
            return true;
        }
    }

    /// <summary>Removes and returns the proposals of a turn, oldest first. Empty when there are none.</summary>
    public IReadOnlyList<GmtProposal> Take(Guid turnId)
    {
        lock (_gate)
        {
            if (!_byTurn.Remove(turnId, out var list))
            {
                return [];
            }

            return list;
        }
    }
}
