using Bazaar.Server.Kanban.Data;

namespace Bazaar.Server.Kanban;

/// <summary>One blocker chip on a schedule item: a task that blocks the item, and whether it is Done.</summary>
public sealed record TeamScheduleBlocker(Guid TaskId, string Title, TaskState State, bool IsDone);

/// <summary>One task on the macro board, with its derived Blocked flag and the blocker chips to show.</summary>
public sealed record TeamScheduleItem(
    Guid TaskId,
    string Title,
    TaskState State,
    DateTimeOffset? DueAt,
    bool IsBlocked,
    IReadOnlyList<TeamScheduleBlocker> Blockers);

/// <summary>The tasks of one owner, in due-date order. A null owner is the unassigned group.</summary>
public sealed record TeamScheduleGroup(Guid? OwnerAgentId, IReadOnlyList<TeamScheduleItem> Items);

/// <summary>The team schedule view model: one group per owner, with the unassigned group last.</summary>
public sealed record TeamSchedule(IReadOnlyList<TeamScheduleGroup> Groups);

/// <summary>
/// Pure builder for the RyuseiFuda layer 1 macro board. Groups every task given by owner, orders deterministically,
/// and derives Blocked and blocker chips from the edges. No I/O and no clock. Does not filter by state.
/// </summary>
public static class TeamScheduleBuilder
{
    /// <summary>Builds the schedule. Blocker chips come from edges whose blocker is in <paramref name="tasks"/>; others are skipped.</summary>
    public static TeamSchedule Build(IReadOnlyList<TaskRecord> tasks, IReadOnlyList<TaskEdge> edges)
    {
        var states = new Dictionary<Guid, TaskState>();
        var byId = new Dictionary<Guid, TaskRecord>();
        foreach (var task in tasks)
        {
            states[task.Id] = task.State;
            byId[task.Id] = task;
        }

        var groups = tasks
            .GroupBy(t => t.OwnerAgentId)
            .OrderBy(g => g.Key is null ? 1 : 0)
            .ThenBy(g => g.Key is null ? string.Empty : g.Key.Value.ToString("D"), StringComparer.Ordinal)
            .Select(g => new TeamScheduleGroup(
                g.Key,
                g.OrderBy(t => t.DueAt is null ? 1 : 0)
                    .ThenBy(t => t.DueAt?.UtcTicks ?? 0)
                    .ThenBy(t => t.Title, StringComparer.Ordinal)
                    .ThenBy(t => t.Id.ToString("D"), StringComparer.Ordinal)
                    .Select(t => BuildItem(t, edges, states, byId))
                    .ToList()))
            .ToList();

        return new TeamSchedule(groups);
    }

    private static TeamScheduleItem BuildItem(
        TaskRecord task,
        IReadOnlyList<TaskEdge> edges,
        IReadOnlyDictionary<Guid, TaskState> states,
        Dictionary<Guid, TaskRecord> byId)
    {
        var blockers = edges
            .Where(e => e.BlockedId == task.Id && byId.ContainsKey(e.BlockerId))
            .Select(e => byId[e.BlockerId])
            .OrderBy(b => b.Id.ToString("D"), StringComparer.Ordinal)
            .Select(b => new TeamScheduleBlocker(b.Id, b.Title, b.State, b.State == TaskState.Done))
            .ToList();

        return new TeamScheduleItem(
            task.Id,
            task.Title,
            task.State,
            task.DueAt,
            BlockedStatus.IsBlocked(task.Id, edges, states),
            blockers);
    }
}
