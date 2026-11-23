using Bazaar.RyuseiFuda.Kanban.Data;
using Bazaar.Server.Health;

namespace Bazaar.RyuseiFuda.Kanban.Health;

/// <summary>
/// Kanban facts for project health (doc 25 morale signals <c>blocked_share</c> and <c>reopened_tasks</c>; doc 12). Pure: no storage, no clock.
/// </summary>
public static class KanbanHealthFacts
{
    /// <summary>
    /// Reopened cards: a move out of Done into any other column. Blocked share counts OPEN cards (every column but Done) and how many
    /// of them are blocked right now (derived from edges, see <see cref="BlockedStatus"/>). A board with no open card has no share:
    /// that is a gap, so <see cref="HealthFacts.Board"/> is null.
    /// </summary>
    public static HealthFacts Build(
        IReadOnlyList<TaskRecord> tasks,
        IReadOnlyList<TaskEdge> edges,
        IReadOnlyList<TaskTransitionFact> transitions,
        bool includeBoard)
    {
        ArgumentNullException.ThrowIfNull(tasks);
        ArgumentNullException.ThrowIfNull(edges);
        ArgumentNullException.ThrowIfNull(transitions);

        var reopens = transitions
            .Where(t => t.From == "done" && t.To != "done")
            .Select(t => new ReopenFact(t.TaskId.ToString("D"), t.At))
            .ToList();

        BoardCountsFact? board = null;
        if (includeBoard)
        {
            var states = tasks.ToDictionary(t => t.Id, t => t.State);
            var open = tasks.Where(t => t.State != TaskState.Done).ToList();
            if (open.Count > 0)
                board = new BoardCountsFact(open.Count(t => BlockedStatus.IsBlocked(t.Id, edges, states)), open.Count);
        }

        return new HealthFacts { Reopens = reopens, Board = board };
    }
}

/// <summary>
/// The Kanban module's health feeder. The health project id is the board's project id (a GUID); any other id is a gap. Column moves
/// are read for the UTC day. The board snapshot is a "now" fact, so it is only reported for today: a past day gets no board share.
/// </summary>
public sealed class KanbanHealthFactSource(TaskRepository tasks, TaskEdgeRepository edges, TaskEventRepository events, TimeProvider timeProvider) : IHealthFactSource
{
    public async Task<HealthFacts> ReadAsync(string projectId, DateOnly day, CancellationToken cancellationToken = default)
    {
        if (!Guid.TryParse(projectId, out var project))
            return HealthFacts.Empty;

        var start = new DateTimeOffset(day.ToDateTime(TimeOnly.MinValue), TimeSpan.Zero);
        var today = DateOnly.FromDateTime(timeProvider.GetUtcNow().UtcDateTime);
        var transitions = await events.ListTransitionsAsync(project, start, start.AddDays(1), cancellationToken).ConfigureAwait(false);

        if (day != today)
            return KanbanHealthFacts.Build([], [], transitions, includeBoard: false);

        var board = await tasks.ListByProjectAsync(project, ct: cancellationToken).ConfigureAwait(false);
        var projectEdges = await edges.ListForProjectAsync(project, ct: cancellationToken).ConfigureAwait(false);
        return KanbanHealthFacts.Build(board, projectEdges, transitions, includeBoard: true);
    }
}

/// <summary>Every project that has a task is a health project, by its project id.</summary>
public sealed class KanbanHealthProjectSource(TaskRepository tasks) : IHealthProjectSource
{
    public async Task<IReadOnlyList<string>> ListProjectsAsync(CancellationToken cancellationToken = default) =>
        [.. (await tasks.ListProjectIdsAsync(cancellationToken).ConfigureAwait(false)).Select(id => id.ToString("D"))];
}
