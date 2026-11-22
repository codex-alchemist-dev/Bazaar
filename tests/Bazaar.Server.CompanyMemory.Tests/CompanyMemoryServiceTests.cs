using Bazaar.Server.CompanyMemory;

namespace Bazaar.Server.CompanyMemory.Tests;

/// <summary>Company Memory (doc 25): one agent records a decision, another retrieves its chain.</summary>
public sealed class CompanyMemoryServiceTests
{
    private static RecordDecisionRequest Request(string slug, params string[] supersedes) =>
        new(Guid.NewGuid(), slug, $"Decision {slug}", $"We decided {slug}.", "agent-ana", supersedes);

    [Fact]
    public async Task An_agent_records_a_decision_and_another_retrieves_its_chain()
    {
        var store = new InMemoryCompanyDecisionStore();
        var writer = new CompanyMemoryService(store);
        var reader = new CompanyMemoryService(store);

        var first = await writer.RecordDecisionAsync(Request("use-polling"), TestContext.Current.CancellationToken);
        var second = await writer.RecordDecisionAsync(Request("use-notify", "0001"), TestContext.Current.CancellationToken);
        var third = await writer.RecordDecisionAsync(Request("use-notify-v2", second.Path), TestContext.Current.CancellationToken);

        var view = await reader.GetChainAsync("0002", TestContext.Current.CancellationToken);

        Assert.NotNull(view);
        Assert.Equal(second.Path, view.Focus.Path);
        Assert.Equal([first.Path], view.Older);
        Assert.Equal([third.Path], view.Newer);
        Assert.Equal([third.Path], view.Current);
        Assert.Equal(2, view.Links.Count);
    }

    [Fact]
    public async Task Recording_twice_with_the_same_id_writes_one_decision()
    {
        var memory = new CompanyMemoryService(new InMemoryCompanyDecisionStore());
        var request = Request("use-polling");

        var first = await memory.RecordDecisionAsync(request, TestContext.Current.CancellationToken);
        var again = await memory.RecordDecisionAsync(request, TestContext.Current.CancellationToken);

        Assert.False(first.AlreadyRecorded);
        Assert.True(again.AlreadyRecorded);
        Assert.Equal(first.Path, again.Path);
    }

    [Fact]
    public async Task Superseding_a_decision_that_does_not_exist_is_refused()
    {
        var memory = new CompanyMemoryService(new InMemoryCompanyDecisionStore());

        await Assert.ThrowsAsync<ArgumentException>(() => memory.RecordDecisionAsync(Request("use-notify", "0007"), TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task A_decision_with_no_links_has_an_empty_chain_and_is_current()
    {
        var memory = new CompanyMemoryService(new InMemoryCompanyDecisionStore());
        var only = await memory.RecordDecisionAsync(Request("use-polling"), TestContext.Current.CancellationToken);

        var view = await memory.GetChainAsync("decisions/0001-use-polling.md", TestContext.Current.CancellationToken);

        Assert.NotNull(view);
        Assert.Empty(view.Older);
        Assert.Empty(view.Newer);
        Assert.Equal([only.Path], view.Current);
    }

    [Fact]
    public async Task An_unknown_reference_gives_no_chain()
    {
        var memory = new CompanyMemoryService(new InMemoryCompanyDecisionStore());

        Assert.Null(await memory.GetChainAsync("0042", TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task A_decision_replacing_two_others_lists_both_nearest_first()
    {
        var memory = new CompanyMemoryService(new InMemoryCompanyDecisionStore());
        await memory.RecordDecisionAsync(Request("a"), TestContext.Current.CancellationToken);
        await memory.RecordDecisionAsync(Request("b", "0001"), TestContext.Current.CancellationToken);
        await memory.RecordDecisionAsync(Request("c", "0002"), TestContext.Current.CancellationToken);
        await memory.RecordDecisionAsync(Request("d", "0003", "0001"), TestContext.Current.CancellationToken);

        var view = await memory.GetChainAsync("0004", TestContext.Current.CancellationToken);

        Assert.NotNull(view);
        Assert.Equal(
            ["decisions/0001-a.md", "decisions/0002-b.md", "decisions/0003-c.md"],
            view.Older.Order(StringComparer.Ordinal));
    }

    [Fact]
    public async Task A_cycle_in_the_store_does_not_loop()
    {
        var store = new FixedStore(
            new Bazaar.Server.Knowledge.CompanyMemory.DecisionRecord("decisions/0001-a.md", "accepted", ["0002-b"], null, null),
            new Bazaar.Server.Knowledge.CompanyMemory.DecisionRecord("decisions/0002-b.md", "accepted", ["0001-a"], null, null));
        var memory = new CompanyMemoryService(store);

        var view = await memory.GetChainAsync("0001", TestContext.Current.CancellationToken);

        Assert.NotNull(view);
        Assert.Equal(["decisions/0002-b.md"], view.Older);
    }

    [Fact]
    public async Task A_missing_target_stays_in_the_chain_and_is_flagged()
    {
        var store = new FixedStore(new Bazaar.Server.Knowledge.CompanyMemory.DecisionRecord("decisions/0002-b.md", "accepted", ["0001-gone"], null, null));
        var memory = new CompanyMemoryService(store);

        var view = await memory.GetChainAsync("0002", TestContext.Current.CancellationToken);

        Assert.NotNull(view);
        Assert.Equal(["decisions/0001-gone.md"], view.Older);
        Assert.True(view.Links.Single().ToMissing);
        Assert.Equal(["decisions/0002-b.md"], view.Current);
    }

    private sealed class FixedStore(params Bazaar.Server.Knowledge.CompanyMemory.DecisionRecord[] records) : ICompanyDecisionStore
    {
        public Task<IReadOnlyList<Bazaar.Server.Knowledge.CompanyMemory.DecisionRecord>> ListAsync(CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<Bazaar.Server.Knowledge.CompanyMemory.DecisionRecord>>(records);

        public Task<RecordedDecision> CreateAsync(RecordDecisionRequest request, CancellationToken ct) =>
            throw new NotSupportedException();
    }
}
