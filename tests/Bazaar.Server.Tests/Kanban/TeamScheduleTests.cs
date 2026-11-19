using Bazaar.Server.Kanban;
using Bazaar.Server.Kanban.Data;

namespace Bazaar.Server.Tests.Kanban;

public class TeamScheduleTests
{
    private static Guid Id(int n) => new(n, 0, 0, new byte[8]);

    private static TaskEdge Edge(int blocker, int blocked) => new(Id(blocker), Id(blocked));

    private static readonly DateTimeOffset _day1 = new(2026, 11, 1, 0, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset _day2 = new(2026, 11, 2, 0, 0, 0, TimeSpan.Zero);

    private static TaskRecord Task(
        int id,
        string title,
        TaskState state = TaskState.Backlog,
        int? owner = null,
        DateTimeOffset? dueAt = null) =>
        new(
            Id(id),
            Id(900),
            title,
            state,
            owner is null ? null : Id(owner.Value),
            TaskGate.None,
            Id(800),
            null,
            dueAt,
            null,
            null,
            1,
            _day1,
            _day1);

    [Fact]
    public void Empty_input_gives_empty_groups()
    {
        var schedule = TeamScheduleBuilder.Build([], []);

        Assert.Empty(schedule.Groups);
    }

    [Fact]
    public void Tasks_are_grouped_by_owner_in_owner_id_order()
    {
        var tasks = new[]
        {
            Task(1, "a", owner: 5),
            Task(2, "b", owner: 3),
            Task(3, "c", owner: 5),
        };

        var schedule = TeamScheduleBuilder.Build(tasks, []);

        Guid?[] owners = [Id(3), Id(5)];
        Assert.Equal(owners, schedule.Groups.Select(g => g.OwnerAgentId));
        Assert.Equal([Id(2)], schedule.Groups[0].Items.Select(i => i.TaskId));
        Assert.Equal([Id(1), Id(3)], schedule.Groups[1].Items.Select(i => i.TaskId));
    }

    [Fact]
    public void Unassigned_group_comes_last()
    {
        var tasks = new[]
        {
            Task(1, "unowned"),
            Task(2, "owned", owner: 9),
            Task(3, "also owned", owner: 1),
        };

        var schedule = TeamScheduleBuilder.Build(tasks, []);

        Assert.Equal(3, schedule.Groups.Count);
        Guid?[] owners = [Id(1), Id(9), null];
        Assert.Equal(owners, schedule.Groups.Select(g => g.OwnerAgentId));
        Assert.Equal([Id(1)], schedule.Groups[^1].Items.Select(i => i.TaskId));
    }

    [Fact]
    public void Items_are_ordered_by_due_date_with_nulls_last()
    {
        var tasks = new[]
        {
            Task(1, "no date", owner: 1),
            Task(2, "later", owner: 1, dueAt: _day2),
            Task(3, "earlier", owner: 1, dueAt: _day1),
        };

        var items = TeamScheduleBuilder.Build(tasks, []).Groups.Single().Items;

        Assert.Equal([Id(3), Id(2), Id(1)], items.Select(i => i.TaskId));
    }

    [Fact]
    public void Equal_due_dates_break_ties_by_title_then_task_id()
    {
        var tasks = new[]
        {
            Task(5, "beta", owner: 1, dueAt: _day1),
            Task(7, "alpha", owner: 1, dueAt: _day1),
            Task(2, "beta", owner: 1, dueAt: _day1),
            Task(9, "beta", owner: 1),
            Task(4, "beta", owner: 1),
        };

        var items = TeamScheduleBuilder.Build(tasks, []).Groups.Single().Items;

        Assert.Equal([Id(7), Id(2), Id(5), Id(4), Id(9)], items.Select(i => i.TaskId));
    }

    [Fact]
    public void Blocker_chips_carry_done_state_and_are_ordered_by_task_id()
    {
        var tasks = new[]
        {
            Task(1, "blocked", owner: 1),
            Task(2, "done blocker", TaskState.Done, owner: 2),
            Task(3, "open blocker", TaskState.Review, owner: 2),
        };
        var edges = new[] { Edge(3, 1), Edge(2, 1) };

        var item = TeamScheduleBuilder.Build(tasks, edges).Groups[0].Items.Single();

        Assert.Equal(
            [
                new TeamScheduleBlocker(Id(2), "done blocker", TaskState.Done, IsDone: true),
                new TeamScheduleBlocker(Id(3), "open blocker", TaskState.Review, IsDone: false),
            ],
            item.Blockers);
    }

    [Fact]
    public void Blocker_missing_from_input_is_skipped_from_chips()
    {
        var tasks = new[] { Task(1, "blocked", owner: 1) };
        var edges = new[] { Edge(42, 1) };

        var item = TeamScheduleBuilder.Build(tasks, edges).Groups.Single().Items.Single();

        Assert.Empty(item.Blockers);
    }

    [Fact]
    public void Is_blocked_while_an_unfinished_blocker_exists()
    {
        var tasks = new[]
        {
            Task(1, "waits", owner: 1),
            Task(2, "open", TaskState.InProgress, owner: 1),
            Task(3, "free", owner: 1),
            Task(4, "done", TaskState.Done, owner: 1),
            Task(5, "only done blocker", owner: 1),
        };
        var edges = new[] { Edge(2, 1), Edge(4, 5) };

        var items = TeamScheduleBuilder.Build(tasks, edges).Groups.Single().Items.ToDictionary(i => i.TaskId);

        Assert.True(items[Id(1)].IsBlocked);
        Assert.False(items[Id(3)].IsBlocked);
        Assert.False(items[Id(5)].IsBlocked);
    }

    [Fact]
    public void Task_with_only_done_blockers_is_not_blocked()
    {
        var tasks = new[]
        {
            Task(1, "finished blocker", TaskState.Done, owner: 1),
            Task(2, "dependent", owner: 1),
        };
        var edges = new[] { Edge(1, 2) };

        var dependent = TeamScheduleBuilder.Build(tasks, edges).Groups.Single().Items.Single(i => i.TaskId == Id(2));

        Assert.False(dependent.IsBlocked);
        Assert.True(dependent.Blockers.Single().IsDone);
    }

    [Fact]
    public void Tasks_in_every_state_are_included()
    {
        var tasks = new[]
        {
            Task(1, "backlog", TaskState.Backlog, owner: 1),
            Task(2, "done", TaskState.Done, owner: 1),
        };

        var items = TeamScheduleBuilder.Build(tasks, []).Groups.Single().Items;

        Assert.Equal(2, items.Count);
    }
}
