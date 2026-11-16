namespace Bazaar.Server.Kanban;

/// <summary>Result of <see cref="BlockedStatus.CheckStart"/>: a refusal names the unfinished blockers.</summary>
public sealed record StartCheck(bool Allowed, IReadOnlyList<Guid> UnfinishedBlockers);

/// <summary>
/// Derived Blocked marking. Blocked is never stored: a task is blocked while any of its blockers is not Done.
/// A blocker id missing from <c>states</c> counts as unfinished (conservative: unknown is not Done).
/// </summary>
public static class BlockedStatus
{
    /// <summary>Blockers of <paramref name="taskId"/> that are not Done, in edge order.</summary>
    public static IReadOnlyList<Guid> UnfinishedBlockers(
        Guid taskId,
        IEnumerable<TaskEdge> edges,
        IReadOnlyDictionary<Guid, TaskState> states) =>
        edges
            .Where(e => e.BlockedId == taskId && !IsDone(e.BlockerId, states))
            .Select(e => e.BlockerId)
            .ToList();

    /// <summary>True while the task has at least one unfinished blocker, whatever column it is in.</summary>
    public static bool IsBlocked(
        Guid taskId,
        IEnumerable<TaskEdge> edges,
        IReadOnlyDictionary<Guid, TaskState> states) =>
        edges.Any(e => e.BlockedId == taskId && !IsDone(e.BlockerId, states));

    /// <summary>
    /// Ready to In progress guard: refused while any blocker is unfinished. Nothing changes on refusal.
    /// The blocked-start role setting (<c>kanban.blocked_start_roles</c>) is not applied here; see the PR notes.
    /// </summary>
    public static StartCheck CheckStart(
        Guid taskId,
        IEnumerable<TaskEdge> edges,
        IReadOnlyDictionary<Guid, TaskState> states)
    {
        var unfinished = UnfinishedBlockers(taskId, edges, states);
        return new StartCheck(unfinished.Count == 0, unfinished);
    }

    /// <summary>
    /// Backlog dependents that move to Ready because <paramref name="completedTaskId"/> just became Done and
    /// they now have no unfinished blocker. <paramref name="statesAfterCompletion"/> must already show the completion.
    /// Returns nothing when auto-Ready is off or the completed task is not Done. A Backlog task with no blockers is never returned.
    /// </summary>
    public static IReadOnlyList<Guid> DependentsBecomingReady(
        Guid completedTaskId,
        IEnumerable<TaskEdge> edges,
        IReadOnlyDictionary<Guid, TaskState> statesAfterCompletion,
        bool autoReadyEnabled)
    {
        if (!autoReadyEnabled || !IsDone(completedTaskId, statesAfterCompletion))
        {
            return [];
        }

        return edges
            .Where(e => e.BlockerId == completedTaskId)
            .Select(e => e.BlockedId)
            .Distinct()
            .Where(dependent =>
                statesAfterCompletion.TryGetValue(dependent, out var state)
                && state == TaskState.Backlog
                && UnfinishedBlockers(dependent, edges, statesAfterCompletion).Count == 0)
            .OrderBy(id => id)
            .ToList();
    }

    private static bool IsDone(Guid taskId, IReadOnlyDictionary<Guid, TaskState> states) =>
        states.TryGetValue(taskId, out var state) && state == TaskState.Done;
}
