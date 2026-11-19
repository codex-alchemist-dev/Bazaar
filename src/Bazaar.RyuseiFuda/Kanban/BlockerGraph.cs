namespace Bazaar.Server.Kanban;

/// <summary>Why a candidate blocked-by edge was accepted or refused.</summary>
public enum EdgeCheckOutcome
{
    Allowed,
    SelfEdge,
    Duplicate,
    Cycle,
    TooManyBlockers,
}

/// <summary>
/// Result of <see cref="BlockerGraph.CheckNewEdge"/>. <see cref="CyclePath"/> is the existing chain
/// from the blocked task back to the blocker (inclusive at both ends) when the outcome is <see cref="EdgeCheckOutcome.Cycle"/>; otherwise empty.
/// </summary>
public sealed record EdgeCheck(EdgeCheckOutcome Outcome, IReadOnlyList<Guid> CyclePath);

/// <summary>
/// Pure blocked-by graph rules: self-edges, duplicates, cycles and the per-task blocker limit.
/// No I/O. The database layer must run the same check under a per-project lock so concurrent inserts cannot both pass.
/// </summary>
public static class BlockerGraph
{
    /// <summary>Shipped default for <c>kanban.max_blockers_per_task</c> (docs/design/12, Settings).</summary>
    public const int DefaultMaxBlockersPerTask = 50;

    /// <summary>Checks whether <paramref name="candidate"/> may be added to <paramref name="existing"/>.</summary>
    public static EdgeCheck CheckNewEdge(
        IEnumerable<TaskEdge> existing,
        TaskEdge candidate,
        int maxBlockersPerTask = DefaultMaxBlockersPerTask)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(maxBlockersPerTask, 1);

        if (candidate.BlockerId == candidate.BlockedId)
        {
            return new EdgeCheck(EdgeCheckOutcome.SelfEdge, []);
        }

        var edges = existing as IReadOnlyCollection<TaskEdge> ?? existing.ToList();

        if (edges.Contains(candidate))
        {
            return new EdgeCheck(EdgeCheckOutcome.Duplicate, []);
        }

        // Adding "B blocks C" closes a cycle exactly when C already blocks B, directly or through a chain.
        var path = FindBlockingPath(edges, from: candidate.BlockedId, to: candidate.BlockerId);
        if (path is not null)
        {
            return new EdgeCheck(EdgeCheckOutcome.Cycle, path);
        }

        var blockersOfBlocked = edges.Count(e => e.BlockedId == candidate.BlockedId);
        if (blockersOfBlocked >= maxBlockersPerTask)
        {
            return new EdgeCheck(EdgeCheckOutcome.TooManyBlockers, []);
        }

        return new EdgeCheck(EdgeCheckOutcome.Allowed, []);
    }

    /// <summary>Breadth-first search along "blocks" direction. Returns the chain start..target, or null when unreachable.</summary>
    private static List<Guid>? FindBlockingPath(IReadOnlyCollection<TaskEdge> edges, Guid from, Guid to)
    {
        var blockedBy = new Dictionary<Guid, List<Guid>>();
        foreach (var edge in edges)
        {
            if (!blockedBy.TryGetValue(edge.BlockerId, out var blocked))
            {
                blocked = [];
                blockedBy[edge.BlockerId] = blocked;
            }

            blocked.Add(edge.BlockedId);
        }

        var parent = new Dictionary<Guid, Guid> { [from] = from };
        var queue = new Queue<Guid>();
        queue.Enqueue(from);

        while (queue.Count > 0)
        {
            var node = queue.Dequeue();
            if (node == to)
            {
                var path = new List<Guid>();
                for (var cursor = to; ; cursor = parent[cursor])
                {
                    path.Add(cursor);
                    if (cursor == from)
                    {
                        break;
                    }
                }

                path.Reverse();
                return path;
            }

            if (!blockedBy.TryGetValue(node, out var next))
            {
                continue;
            }

            foreach (var successor in next.Where(successor => !parent.ContainsKey(successor)))
            {
                parent[successor] = node;
                queue.Enqueue(successor);
            }
        }

        return null;
    }
}
