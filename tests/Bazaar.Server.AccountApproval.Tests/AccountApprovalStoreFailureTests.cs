using Bazaar.Server.AccountApproval.Abstractions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Bazaar.Server.AccountApproval.Tests;

/// <summary>A store that wraps another and throws on the operations switched on. Nothing is stored by a call that throws.</summary>
internal sealed class ThrowingStore(IAccountApprovalStore inner) : IAccountApprovalStore
{
    public bool ThrowOnRead { get; set; }

    public bool ThrowOnWrite { get; set; }

    public Task<AccountApprovalRecord?> GetAsync(string requestId, CancellationToken ct = default) =>
        ThrowOnRead ? throw new InvalidOperationException("store down") : inner.GetAsync(requestId, ct);

    public Task<bool> TryCreateAsync(AccountApprovalRequest request, AccountApprovalDecision decision, CancellationToken ct = default) =>
        ThrowOnWrite ? throw new InvalidOperationException("store down") : inner.TryCreateAsync(request, decision, ct);

    public Task<StoredVoteOutcome> TryAppendVoteAsync(PeerVote vote, AccountApprovalDecision decision, CancellationToken ct = default) =>
        ThrowOnWrite ? throw new InvalidOperationException("store down") : inner.TryAppendVoteAsync(vote, decision, ct);

    public Task<bool> TryDecideAsync(string requestId, AccountApprovalDecision decision, CancellationToken ct = default) =>
        ThrowOnWrite ? throw new InvalidOperationException("store down") : inner.TryDecideAsync(requestId, decision, ct);
}

/// <summary>Any store error means the request is not approved (fail closed).</summary>
public class AccountApprovalStoreFailureTests
{
    private static (Harness Harness, ThrowingStore Store, InMemoryAccountApprovalStore Inner) Build()
    {
        var inner = new InMemoryAccountApprovalStore();
        var store = new ThrowingStore(inner);
        return (new Harness(store: store), store, inner);
    }

    [Fact]
    public async Task A_store_that_cannot_be_read_approves_nothing_and_audits_nothing()
    {
        var (h, store, _) = Build();
        store.ThrowOnRead = true;

        await Assert.ThrowsAsync<InvalidOperationException>(() => h.Service.SubmitAsync(Fixtures.Request(), TestContext.Current.CancellationToken));
        await Assert.ThrowsAsync<InvalidOperationException>(() => h.Service.RecordVoteAsync(Fixtures.Vote("agent-a"), TestContext.Current.CancellationToken));
        await Assert.ThrowsAsync<InvalidOperationException>(() => h.Service.ReevaluateAsync("req-1", TestContext.Current.CancellationToken));
        await Assert.ThrowsAsync<InvalidOperationException>(() => h.Service.GetAsync("req-1", TestContext.Current.CancellationToken));

        Assert.Empty(h.Audit.Entries);
        Assert.Empty(h.HandOff.Requests);
    }

    [Fact]
    public async Task A_failed_create_stores_no_request_and_returns_no_decision()
    {
        var (h, store, inner) = Build();
        store.ThrowOnWrite = true;

        await Assert.ThrowsAsync<InvalidOperationException>(() => h.Service.SubmitAsync(Fixtures.Request(), TestContext.Current.CancellationToken));

        Assert.Null(await inner.GetAsync("req-1", TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task A_failed_write_of_the_deciding_vote_leaves_the_request_pending_not_approved()
    {
        var (h, store, inner) = Build();
        var ct = TestContext.Current.CancellationToken;
        await h.Service.SubmitAsync(Fixtures.Request(), ct);
        await h.Service.RecordVoteAsync(Fixtures.Vote("agent-a"), ct);
        store.ThrowOnWrite = true;

        await Assert.ThrowsAsync<InvalidOperationException>(() => h.Service.RecordVoteAsync(Fixtures.Vote("agent-b"), ct));

        var record = await inner.GetAsync("req-1", ct);
        Assert.IsType<Pending>(record!.Decision);
        Assert.Single(record.Votes);
    }

    [Fact]
    public async Task A_failed_write_during_reevaluation_leaves_the_request_pending()
    {
        var (h, store, inner) = Build();
        var ct = TestContext.Current.CancellationToken;
        await h.Service.SubmitAsync(Fixtures.Request(), ct);
        h.Time.Current = Fixtures.Now.AddHours(h.Options.ApprovalExpiryHours + 1);
        store.ThrowOnWrite = true;

        await Assert.ThrowsAsync<InvalidOperationException>(() => h.Service.ReevaluateAsync("req-1", ct));

        Assert.IsType<Pending>((await inner.GetAsync("req-1", ct))!.Decision);
    }

    [Fact]
    public async Task The_service_holds_no_state_of_its_own_so_a_new_instance_over_the_store_sees_the_request()
    {
        var h = new Harness();
        var ct = TestContext.Current.CancellationToken;
        await h.Service.SubmitAsync(Fixtures.Request(), ct);
        await h.Service.RecordVoteAsync(Fixtures.Vote("agent-a"), ct);

        using var restarted = h.Restart();

        var pending = Assert.IsType<Pending>(await restarted.GetAsync("req-1", ct));
        Assert.Equal(1, pending.Remaining);
        var second = await restarted.RecordVoteAsync(Fixtures.Vote("agent-b"), ct);
        Assert.IsType<ApprovedByPeers>(second.Decision);
    }

    [Fact]
    public void The_component_registers_the_in_memory_store_by_default_and_a_registered_store_wins()
    {
        var configuration = new ConfigurationBuilder().Build();
        var byDefault = new ServiceCollection();
        new AccountApprovalComponent().AddServices(byDefault, configuration);
        var mine = new InMemoryAccountApprovalStore();
        var custom = new ServiceCollection();
        custom.AddSingleton<IAccountApprovalStore>(mine);
        new AccountApprovalComponent().AddServices(custom, configuration);

        using var defaultProvider = byDefault.BuildServiceProvider();
        using var customProvider = custom.BuildServiceProvider();
        Assert.IsType<InMemoryAccountApprovalStore>(defaultProvider.GetRequiredService<IAccountApprovalStore>());
        Assert.Same(mine, customProvider.GetRequiredService<IAccountApprovalStore>());
    }
}
