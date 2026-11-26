using Bazaar.OfflineSync;
using Bazaar.Server.OfflineSync.Rules;

namespace Bazaar.Server.OfflineSync.Tests.Rules;

public sealed class KanbanEdgeRuleTests
{
    private const string _graph = "board-1";

    private static (KanbanEdgeRule Rule, InMemoryKanbanEdgeState State, RecordingReviewSink Review) Build()
    {
        var state = new InMemoryKanbanEdgeState();
        var review = new RecordingReviewSink();
        return (new KanbanEdgeRule(state, review), state, review);
    }

    private static OfflineWrite Edge(string from, string to, bool remove = false, string? baseVersion = null, Guid? writeId = null) =>
        RuleTestKit.Write(OfflineDataClass.KanbanEdge, _graph, RuleTestKit.Json(new KanbanEdgePayload(from, to, remove)), baseVersion, writeId);

    [Fact]
    public async Task Add_is_applied_and_the_edge_is_present()
    {
        var (rule, state, _) = Build();

        var outcome = await rule.MergeAsync(Edge("A", "B"), CancellationToken.None);

        Assert.Equal(OfflineOutcomeKind.Applied, outcome.Kind);
        Assert.Equal("1", outcome.ServerVersion);
        Assert.Contains(new KanbanEdge("A", "B"), await state.GetEdgesAsync(_graph, CancellationToken.None));
    }

    [Fact]
    public async Task Edge_that_closes_a_two_cycle_is_rejected()
    {
        var (rule, state, _) = Build();
        await rule.MergeAsync(Edge("A", "B"), CancellationToken.None);

        var closing = await rule.MergeAsync(Edge("B", "A"), CancellationToken.None);

        Assert.Equal(OfflineOutcomeKind.Rejected, closing.Kind);
        Assert.Single(await state.GetEdgesAsync(_graph, CancellationToken.None));
    }

    [Fact]
    public async Task Transitive_cycle_is_rejected()
    {
        var (rule, state, _) = Build();
        await rule.MergeAsync(Edge("A", "B"), CancellationToken.None);
        await rule.MergeAsync(Edge("B", "C"), CancellationToken.None);
        await rule.MergeAsync(Edge("C", "D"), CancellationToken.None);

        var closing = await rule.MergeAsync(Edge("D", "A"), CancellationToken.None);

        Assert.Equal(OfflineOutcomeKind.Rejected, closing.Kind);
        Assert.Equal(3, (await state.GetEdgesAsync(_graph, CancellationToken.None)).Count);
    }

    [Fact]
    public async Task Self_edge_is_rejected()
    {
        var (rule, _, _) = Build();

        var outcome = await rule.MergeAsync(Edge("A", "A"), CancellationToken.None);

        Assert.Equal(OfflineOutcomeKind.Rejected, outcome.Kind);
    }

    [Fact]
    public async Task Adding_an_existing_edge_is_a_no_op_and_keeps_its_version()
    {
        var (rule, state, _) = Build();
        await rule.MergeAsync(Edge("A", "B"), CancellationToken.None);

        var again = await rule.MergeAsync(Edge("A", "B"), CancellationToken.None);

        Assert.Equal(OfflineOutcomeKind.Applied, again.Kind);
        Assert.Equal("1", again.ServerVersion);
        Assert.Single(await state.GetEdgesAsync(_graph, CancellationToken.None));
    }

    [Fact]
    public async Task Remove_with_the_current_base_version_is_applied()
    {
        var (rule, state, _) = Build();
        await rule.MergeAsync(Edge("A", "B"), CancellationToken.None);

        var remove = await rule.MergeAsync(Edge("A", "B", remove: true, baseVersion: "1"), CancellationToken.None);

        Assert.Equal(OfflineOutcomeKind.Applied, remove.Kind);
        Assert.Equal("2", remove.ServerVersion);
        Assert.Empty(await state.GetEdgesAsync(_graph, CancellationToken.None));
    }

    [Fact]
    public async Task Remove_with_a_stale_base_version_is_a_conflict_with_a_review_item()
    {
        var (rule, state, review) = Build();
        await rule.MergeAsync(Edge("A", "B"), CancellationToken.None);

        var stale = await rule.MergeAsync(Edge("A", "B", remove: true, baseVersion: "0"), CancellationToken.None);

        Assert.Equal(OfflineOutcomeKind.Conflict, stale.Kind);
        Assert.Single(await state.GetEdgesAsync(_graph, CancellationToken.None));
        Assert.Equal("kanban-edge-conflict", Assert.Single(review.Items).Kind);
    }

    [Fact]
    public async Task Remove_without_a_base_version_is_rejected()
    {
        var (rule, _, _) = Build();
        await rule.MergeAsync(Edge("A", "B"), CancellationToken.None);

        var outcome = await rule.MergeAsync(Edge("A", "B", remove: true), CancellationToken.None);

        Assert.Equal(OfflineOutcomeKind.Rejected, outcome.Kind);
    }

    [Fact]
    public async Task Replay_of_an_add_is_a_duplicate()
    {
        var (rule, _, _) = Build();
        var add = Edge("A", "B");
        await rule.MergeAsync(add, CancellationToken.None);

        var replay = await rule.MergeAsync(add, CancellationToken.None);

        Assert.Equal(OfflineOutcomeKind.Duplicate, replay.Kind);
    }
}
