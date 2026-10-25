using Bazaar.Server.Kanban;

namespace Bazaar.Server.Tests.Kanban;

public class BlockedStatusTests
{
    private static Guid Id(int n) => new(n, 0, 0, new byte[8]);

    private static TaskEdge Edge(int blocker, int blocked) => new(Id(blocker), Id(blocked));

    private static Dictionary<Guid, TaskState> States(params (int Id, TaskState State)[] tasks) =>
        tasks.ToDictionary(t => Id(t.Id), t => t.State);

    [Fact]
    public void Task_without_blockers_is_not_blocked()
    {
        var states = States((1, TaskState.Backlog));

        Assert.False(BlockedStatus.IsBlocked(Id(1), [], states));
        Assert.Empty(BlockedStatus.UnfinishedBlockers(Id(1), [], states));
    }

    [Theory]
    [InlineData(TaskState.Backlog)]
    [InlineData(TaskState.Ready)]
    [InlineData(TaskState.InProgress)]
    [InlineData(TaskState.Review)]
    public void Task_is_blocked_while_any_blocker_is_not_done(TaskState blockerState)
    {
        var edges = new[] { Edge(1, 2) };
        var states = States((1, blockerState), (2, TaskState.Backlog));

        Assert.True(BlockedStatus.IsBlocked(Id(2), edges, states));
        Assert.Equal([Id(1)], BlockedStatus.UnfinishedBlockers(Id(2), edges, states));
    }

    [Fact]
    public void Task_is_not_blocked_once_every_blocker_is_done()
    {
        var edges = new[] { Edge(1, 3), Edge(2, 3) };
        var states = States((1, TaskState.Done), (2, TaskState.Done), (3, TaskState.Ready));

        Assert.False(BlockedStatus.IsBlocked(Id(3), edges, states));
        Assert.Empty(BlockedStatus.UnfinishedBlockers(Id(3), edges, states));
    }

    [Fact]
    public void Only_the_unfinished_blockers_are_listed()
    {
        var edges = new[] { Edge(1, 3), Edge(2, 3), Edge(4, 3) };
        var states = States((1, TaskState.Done), (2, TaskState.InProgress), (4, TaskState.Review), (3, TaskState.Ready));

        Assert.Equal([Id(2), Id(4)], BlockedStatus.UnfinishedBlockers(Id(3), edges, states));
    }

    [Fact]
    public void Blocker_missing_from_states_counts_as_unfinished()
    {
        var edges = new[] { Edge(1, 2) };
        var states = States((2, TaskState.Ready));

        Assert.True(BlockedStatus.IsBlocked(Id(2), edges, states));
    }

    [Fact]
    public void Reopening_a_blocker_marks_its_dependent_blocked_again()
    {
        var edges = new[] { Edge(1, 2) };

        Assert.False(BlockedStatus.IsBlocked(Id(2), edges, States((1, TaskState.Done), (2, TaskState.Ready))));
        Assert.True(BlockedStatus.IsBlocked(Id(2), edges, States((1, TaskState.Review), (2, TaskState.Ready))));
    }

    [Fact]
    public void Start_is_refused_with_the_blocker_list_and_allowed_when_clear()
    {
        var edges = new[] { Edge(1, 2), Edge(3, 2) };

        var refused = BlockedStatus.CheckStart(Id(2), edges, States((1, TaskState.Done), (3, TaskState.Review), (2, TaskState.Ready)));
        var allowed = BlockedStatus.CheckStart(Id(2), edges, States((1, TaskState.Done), (3, TaskState.Done), (2, TaskState.Ready)));

        Assert.False(refused.Allowed);
        Assert.Equal([Id(3)], refused.UnfinishedBlockers);
        Assert.True(allowed.Allowed);
        Assert.Empty(allowed.UnfinishedBlockers);
    }

    [Fact]
    public void Last_blocker_done_makes_a_backlog_dependent_ready()
    {
        var edges = new[] { Edge(1, 3), Edge(2, 3) };
        var states = States((1, TaskState.Done), (2, TaskState.Done), (3, TaskState.Backlog));

        var ready = BlockedStatus.DependentsBecomingReady(Id(2), edges, states, autoReadyEnabled: true);

        Assert.Equal([Id(3)], ready);
    }

    [Fact]
    public void Dependent_stays_blocked_while_another_blocker_is_unfinished()
    {
        var edges = new[] { Edge(1, 3), Edge(2, 3) };
        var states = States((1, TaskState.InProgress), (2, TaskState.Done), (3, TaskState.Backlog));

        var ready = BlockedStatus.DependentsBecomingReady(Id(2), edges, states, autoReadyEnabled: true);

        Assert.Empty(ready);
    }

    [Fact]
    public void Auto_ready_off_moves_nothing()
    {
        var edges = new[] { Edge(1, 2) };
        var states = States((1, TaskState.Done), (2, TaskState.Backlog));

        Assert.Empty(BlockedStatus.DependentsBecomingReady(Id(1), edges, states, autoReadyEnabled: false));
    }

    [Fact]
    public void Dependent_not_in_backlog_is_not_moved()
    {
        var edges = new[] { Edge(1, 2) };
        var states = States((1, TaskState.Done), (2, TaskState.InProgress));

        Assert.Empty(BlockedStatus.DependentsBecomingReady(Id(1), edges, states, autoReadyEnabled: true));
    }

    [Fact]
    public void Nothing_moves_when_the_completed_task_is_not_done()
    {
        var edges = new[] { Edge(1, 2) };
        var states = States((1, TaskState.Review), (2, TaskState.Backlog));

        Assert.Empty(BlockedStatus.DependentsBecomingReady(Id(1), edges, states, autoReadyEnabled: true));
    }
}
