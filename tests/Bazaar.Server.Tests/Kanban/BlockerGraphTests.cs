using Bazaar.Server.Kanban;

namespace Bazaar.Server.Tests.Kanban;

public class BlockerGraphTests
{
    private static Guid Id(int n) => new(n, 0, 0, new byte[8]);

    private static TaskEdge Edge(int blocker, int blocked) => new(Id(blocker), Id(blocked));

    [Fact]
    public void Self_edge_is_refused()
    {
        var result = BlockerGraph.CheckNewEdge([], Edge(1, 1));

        Assert.Equal(EdgeCheckOutcome.SelfEdge, result.Outcome);
        Assert.Empty(result.CyclePath);
    }

    [Fact]
    public void Duplicate_edge_is_refused()
    {
        var result = BlockerGraph.CheckNewEdge([Edge(1, 2)], Edge(1, 2));

        Assert.Equal(EdgeCheckOutcome.Duplicate, result.Outcome);
    }

    [Fact]
    public void First_edge_into_an_empty_graph_is_allowed()
    {
        var result = BlockerGraph.CheckNewEdge([], Edge(1, 2));

        Assert.Equal(EdgeCheckOutcome.Allowed, result.Outcome);
        Assert.Empty(result.CyclePath);
    }

    [Fact]
    public void Two_node_cycle_is_refused_with_its_path()
    {
        // A blocks B exists; adding B blocks A closes the loop.
        var result = BlockerGraph.CheckNewEdge([Edge(1, 2)], Edge(2, 1));

        Assert.Equal(EdgeCheckOutcome.Cycle, result.Outcome);
        Assert.Equal([Id(1), Id(2)], result.CyclePath);
    }

    [Fact]
    public void Three_node_cycle_is_refused_with_its_path()
    {
        // A blocks B, B blocks C; adding C blocks A closes A -> B -> C -> A.
        var existing = new[] { Edge(1, 2), Edge(2, 3) };

        var result = BlockerGraph.CheckNewEdge(existing, Edge(3, 1));

        Assert.Equal(EdgeCheckOutcome.Cycle, result.Outcome);
        Assert.Equal([Id(1), Id(2), Id(3)], result.CyclePath);
    }

    [Fact]
    public void Cycle_through_a_longer_chain_is_found_even_when_other_branches_exist()
    {
        // Branch 1 -> 9 is a dead end; the real chain is 1 -> 2 -> 3 -> 4.
        var existing = new[] { Edge(1, 9), Edge(1, 2), Edge(2, 3), Edge(3, 4) };

        var result = BlockerGraph.CheckNewEdge(existing, Edge(4, 1));

        Assert.Equal(EdgeCheckOutcome.Cycle, result.Outcome);
        Assert.Equal([Id(1), Id(2), Id(3), Id(4)], result.CyclePath);
    }

    [Fact]
    public void Diamond_is_not_a_cycle()
    {
        // A blocks B and C; B blocks D; adding C blocks D shares no loop.
        var existing = new[] { Edge(1, 2), Edge(1, 3), Edge(2, 4) };

        var result = BlockerGraph.CheckNewEdge(existing, Edge(3, 4));

        Assert.Equal(EdgeCheckOutcome.Allowed, result.Outcome);
    }

    [Fact]
    public void Edge_pointing_away_from_an_existing_chain_is_not_a_cycle()
    {
        // A blocks B; adding B blocks C is a forward link, not a loop.
        var result = BlockerGraph.CheckNewEdge([Edge(1, 2)], Edge(2, 3));

        Assert.Equal(EdgeCheckOutcome.Allowed, result.Outcome);
    }

    [Fact]
    public void Blocker_limit_refuses_the_edge_that_would_exceed_it()
    {
        var existing = new[] { Edge(1, 9), Edge(2, 9) };

        var atLimit = BlockerGraph.CheckNewEdge(existing, Edge(3, 9), maxBlockersPerTask: 2);
        var underLimit = BlockerGraph.CheckNewEdge(existing, Edge(3, 9), maxBlockersPerTask: 3);

        Assert.Equal(EdgeCheckOutcome.TooManyBlockers, atLimit.Outcome);
        Assert.Equal(EdgeCheckOutcome.Allowed, underLimit.Outcome);
    }

    [Fact]
    public void Default_blocker_limit_is_fifty()
    {
        var existing = Enumerable.Range(1, BlockerGraph.DefaultMaxBlockersPerTask)
            .Select(i => Edge(i, 999))
            .ToList();

        var result = BlockerGraph.CheckNewEdge(existing, Edge(1000, 999));

        Assert.Equal(50, BlockerGraph.DefaultMaxBlockersPerTask);
        Assert.Equal(EdgeCheckOutcome.TooManyBlockers, result.Outcome);
    }

    [Fact]
    public void Cycle_is_reported_before_the_blocker_limit()
    {
        var existing = new[] { Edge(1, 2), Edge(3, 2) };

        var result = BlockerGraph.CheckNewEdge(existing, Edge(2, 1), maxBlockersPerTask: 1);

        Assert.Equal(EdgeCheckOutcome.Cycle, result.Outcome);
    }

    [Fact]
    public void Zero_limit_is_rejected()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            BlockerGraph.CheckNewEdge([], Edge(1, 2), maxBlockersPerTask: 0));
    }
}
