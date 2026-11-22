using Bazaar.Server.Security.Approvals;
using Bazaar.Server.Security.Policy;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;

namespace Bazaar.Server.Security.Tests.Approvals;

public sealed class OwnerApprovalEnqueuerTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static OwnerApprovalEnqueueRequest Req(
        string agent = "agent-a",
        string tool = "account.request",
        string key = "key-1",
        string summary = "Create an account at example.org.") => new(agent, tool, key, summary);

    private static (OwnerApprovalEnqueuer Sut, InMemoryApprovalStore Store, FakeTimeProvider Time) Make()
    {
        var store = new InMemoryApprovalStore();
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 11, 22, 10, 0, 0, TimeSpan.Zero));
        return (new OwnerApprovalEnqueuer(store, new InMemoryRateBucketStore(), new ApprovalOptions(ExpiryHours: 12), time), store, time);
    }

    [Fact]
    public async Task Creates_one_pending_tier_two_approval()
    {
        var (sut, store, time) = Make();
        var id = await sut.EnqueueAsync(Req(), Ct);

        var row = await store.GetAsync(id, Ct);
        Assert.NotNull(row);
        Assert.Equal(ApprovalStatus.Pending, row.Status);
        Assert.Equal("account.request", row.Tool);
        Assert.Equal("agent-a", row.AgentId);
        Assert.Equal(Guid.Empty, row.SessionId);
        Assert.Empty(row.TaintSources);
        Assert.Null(row.Target);
        Assert.Equal(32, row.ArgsHash.Length);
        Assert.Contains("Create an account at example.org.", row.PayloadJson);
        Assert.Equal(time.GetUtcNow(), row.RequestedAt);
        Assert.Equal(time.GetUtcNow().AddHours(12), row.ExpiresAt);
    }

    [Fact]
    public async Task Same_key_twice_gives_the_same_id_and_one_row()
    {
        var (sut, store, _) = Make();
        var first = await sut.EnqueueAsync(Req(), Ct);
        var second = await sut.EnqueueAsync(Req(), Ct);

        Assert.Equal(first, second);
        Assert.Equal(OwnerApprovalEnqueuer.DeriveId("account.request", "key-1"), first);
        Assert.Equal(1, store.Count);
    }

    [Fact]
    public async Task Different_keys_give_different_ids()
    {
        var (sut, store, _) = Make();
        var a = await sut.EnqueueAsync(Req(key: "k1"), Ct);
        var b = await sut.EnqueueAsync(Req(key: "k2"), Ct);

        Assert.NotEqual(a, b);
        Assert.Equal(2, store.Count);
    }

    [Fact]
    public async Task A_decided_key_returns_the_existing_id_and_does_not_reopen()
    {
        var (sut, store, _) = Make();
        var id = await sut.EnqueueAsync(Req(), Ct);
        var row = (await store.GetAsync(id, Ct))!;
        Assert.True(await store.TryReplaceAsync(id, ApprovalStatus.Pending, row with { Status = ApprovalStatus.Expired }, Ct));

        Assert.Equal(id, await sut.EnqueueAsync(Req(), Ct));
        Assert.Equal(ApprovalStatus.Expired, (await store.GetAsync(id, Ct))!.Status);
    }

    [Fact]
    public async Task Concurrent_enqueues_of_one_key_give_one_row()
    {
        var store = new InMemoryApprovalStore();
        var sut = new OwnerApprovalEnqueuer(store, new InMemoryRateBucketStore(), new ApprovalOptions(RequestCapPerAgentHour: 100));
        var ids = await Task.WhenAll(Enumerable.Range(0, 32).Select(_ => Task.Run(() => sut.EnqueueAsync(Req(), Ct), Ct)));

        Assert.Single(ids.Distinct());
        Assert.Equal(1, store.Count);
    }

    [Fact]
    public async Task Losing_the_insert_race_reads_the_winner()
    {
        var inner = new InMemoryApprovalStore();
        var racing = new RacingStore(inner);
        var sut = new OwnerApprovalEnqueuer(racing, new InMemoryRateBucketStore());

        var id = await sut.EnqueueAsync(Req(), Ct);

        Assert.Equal(OwnerApprovalEnqueuer.DeriveId("account.request", "key-1"), id);
        Assert.Equal(1, inner.Count);
    }

    [Fact]
    public async Task An_insert_failure_with_no_row_propagates()
    {
        var sut = new OwnerApprovalEnqueuer(new FailingStore(), new InMemoryRateBucketStore());
        await Assert.ThrowsAsync<TimeoutException>(() => sut.EnqueueAsync(Req(), Ct));
    }

    [Fact]
    public async Task Reusing_a_key_for_a_different_request_throws()
    {
        var (sut, _, _) = Make();
        await sut.EnqueueAsync(Req(), Ct);

        await Assert.ThrowsAsync<InvalidOperationException>(() => sut.EnqueueAsync(Req(agent: "agent-b"), Ct));
        await Assert.ThrowsAsync<InvalidOperationException>(() => sut.EnqueueAsync(Req(summary: "Something else."), Ct));
    }

    [Theory]
    [InlineData("", "account.request", "k", "s")]
    [InlineData("agent-a", "", "k", "s")]
    [InlineData("agent-a", "account.request", " ", "s")]
    [InlineData("agent-a", "account.request", "k", "")]
    [InlineData("agent\n-a", "account.request", "k", "s")]
    [InlineData("agent-a", "account.request", "k\u0007", "s")]
    [InlineData("agent-a", "account.request", "k", "line\none")]
    [InlineData("agent-a", "account.request", " k", "s")]
    [InlineData("agent-a", "Account.Request", "k", "s")]
    [InlineData("agent-a", "accountrequest", "k", "s")]
    [InlineData("agent-a", "account..request", "k", "s")]
    [InlineData("agent-a", "account.request ", "k", "s")]
    [InlineData("agent-a", "account.req-uest", "k", "s")]
    [InlineData("agent-a", "1account.request", "k", "s")]
    public async Task Invalid_input_is_refused_and_stores_nothing(string agent, string tool, string key, string summary)
    {
        var (sut, store, _) = Make();
        await Assert.ThrowsAsync<ArgumentException>(() => sut.EnqueueAsync(new OwnerApprovalEnqueueRequest(agent, tool, key, summary), Ct));
        Assert.Equal(0, store.Count);
    }

    [Fact]
    public async Task Too_long_input_is_refused()
    {
        var (sut, store, _) = Make();
        await Assert.ThrowsAsync<ArgumentException>(() => sut.EnqueueAsync(Req(summary: new string('x', OwnerApprovalEnqueueRequest.MaxSummaryLength + 1)), Ct));
        await Assert.ThrowsAsync<ArgumentException>(() => sut.EnqueueAsync(Req(key: new string('k', OwnerApprovalEnqueueRequest.MaxIdentifierLength + 1)), Ct));
        await Assert.ThrowsAsync<ArgumentException>(() => sut.EnqueueAsync(Req(agent: new string('a', OwnerApprovalEnqueueRequest.MaxIdentifierLength + 1)), Ct));
        Assert.Equal(0, store.Count);

        await sut.EnqueueAsync(Req(summary: new string('x', OwnerApprovalEnqueueRequest.MaxSummaryLength)), Ct);
        Assert.Equal(1, store.Count);
    }

    [Fact]
    public async Task Cancellation_propagates_before_anything_is_stored()
    {
        var (sut, store, _) = Make();
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => sut.EnqueueAsync(Req(), cts.Token));
        Assert.Equal(0, store.Count);
    }

    [Fact]
    public void No_store_means_throw()
    {
        Assert.Throws<ArgumentNullException>(() => new OwnerApprovalEnqueuer(null!, new InMemoryRateBucketStore()));
        Assert.Throws<ArgumentNullException>(() => new OwnerApprovalEnqueuer(new InMemoryApprovalStore(), null!));
        var provider = new ServiceCollection().AddSingleton<IRateBucketStore, InMemoryRateBucketStore>().AddOwnerApprovalEnqueue().BuildServiceProvider();
        Assert.Throws<InvalidOperationException>(() => provider.GetRequiredService<IOwnerApprovalEnqueuer>());
        var noLimiter = new ServiceCollection().AddSingleton<IApprovalStore, InMemoryApprovalStore>().AddOwnerApprovalEnqueue().BuildServiceProvider();
        Assert.Throws<InvalidOperationException>(() => noLimiter.GetRequiredService<IOwnerApprovalEnqueuer>());
    }

    [Fact]
    public void Registration_resolves_when_a_store_is_registered_and_a_host_enqueuer_wins()
    {
        var provider = new ServiceCollection()
            .AddSingleton<IApprovalStore, InMemoryApprovalStore>()
            .AddSingleton<IRateBucketStore, InMemoryRateBucketStore>()
            .AddOwnerApprovalEnqueue()
            .BuildServiceProvider();
        Assert.IsType<OwnerApprovalEnqueuer>(provider.GetRequiredService<IOwnerApprovalEnqueuer>());

        var custom = new FakeEnqueuer();
        var overridden = new ServiceCollection()
            .AddSingleton<IOwnerApprovalEnqueuer>(custom)
            .AddOwnerApprovalEnqueue()
            .BuildServiceProvider();
        Assert.Same(custom, overridden.GetRequiredService<IOwnerApprovalEnqueuer>());
    }

    [Fact]
    public async Task The_row_reads_back_as_a_pending_card_and_takes_the_owners_signed_decision()
    {
        using var key = Signing.NewKey();
        var store = new InMemoryApprovalStore();
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 11, 22, 10, 0, 0, TimeSpan.Zero));
        var sut = new OwnerApprovalEnqueuer(store, new InMemoryRateBucketStore(), new ApprovalOptions(), time);
        var id = await sut.EnqueueAsync(Req(), Ct);
        var row = (await store.GetAsync(id, Ct))!;

        var card = ApprovalCardMapper.ToCard(row, time.GetUtcNow());
        Assert.Equal(id, card.ApprovalId);
        Assert.Equal("account.request", card.Tool);
        Assert.Equal(Bazaar.Contracts.Approvals.ApprovalCardStatus.Pending, card.Status);
        Assert.Contains("Create an account at example.org.", card.PayloadText);

        var service = new ApprovalService(store, Signing.RegistryWith(key), new InMemoryRateBucketStore(), new ApprovalOptions());
        var signature = Signing.Sign(key, id, ApprovalDecision.Approve, row.ArgsHash, row.ExpiresAt, Signing.DeviceId);
        var result = await service.DecideAsync(id, ApprovalDecision.Approve, Signing.DeviceId, signature, time.GetUtcNow(), Ct);
        Assert.Equal(ApprovalService.DecisionOutcome.Recorded, result.Outcome);
        Assert.Equal(ApprovalStatus.Approved, (await store.GetAsync(id, Ct))!.Status);
    }

    [Fact]
    public async Task An_unsigned_or_forged_decision_does_not_approve_the_enqueued_row()
    {
        using var key = Signing.NewKey();
        using var other = Signing.NewKey();
        var store = new InMemoryApprovalStore();
        var sut = new OwnerApprovalEnqueuer(store, new InMemoryRateBucketStore(), new ApprovalOptions(RequestCapPerAgentHour: 100));
        var id = await sut.EnqueueAsync(Req(), Ct);
        var row = (await store.GetAsync(id, Ct))!;

        var service = new ApprovalService(store, Signing.RegistryWith(key), new InMemoryRateBucketStore(), new ApprovalOptions());
        var forged = Signing.Sign(other, id, ApprovalDecision.Approve, row.ArgsHash, row.ExpiresAt, Signing.DeviceId);
        var result = await service.DecideAsync(id, ApprovalDecision.Approve, Signing.DeviceId, forged, DateTimeOffset.UtcNow, Ct);

        Assert.Equal(ApprovalService.DecisionOutcome.InvalidSignature, result.Outcome);
        Assert.Equal(ApprovalStatus.Pending, (await store.GetAsync(id, Ct))!.Status);
    }

    [Fact]
    public async Task The_hourly_cap_is_enforced_per_agent_and_stores_nothing_over_it()
    {
        var store = new InMemoryApprovalStore();
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 11, 22, 10, 0, 0, TimeSpan.Zero));
        var sut = new OwnerApprovalEnqueuer(store, new InMemoryRateBucketStore(), new ApprovalOptions(RequestCapPerAgentHour: 2), time);

        await sut.EnqueueAsync(Req(key: "k1"), Ct);
        await sut.EnqueueAsync(Req(key: "k2"), Ct);
        var ex = await Assert.ThrowsAsync<OwnerApprovalRateLimitedException>(() => sut.EnqueueAsync(Req(key: "k3"), Ct));
        Assert.Equal("agent-a", ex.AgentId);
        Assert.Equal(2, store.Count);

        await sut.EnqueueAsync(Req(agent: "agent-b", key: "k4"), Ct);
        time.Advance(TimeSpan.FromHours(1));
        await sut.EnqueueAsync(Req(key: "k3"), Ct);
        Assert.Equal(4, store.Count);
    }

    [Fact]
    public async Task A_repeat_of_an_existing_key_does_not_consume_budget()
    {
        var sut = new OwnerApprovalEnqueuer(new InMemoryApprovalStore(), new InMemoryRateBucketStore(), new ApprovalOptions(RequestCapPerAgentHour: 1));
        var id = await sut.EnqueueAsync(Req(key: "k1"), Ct);
        for (var i = 0; i < 5; i++)
        {
            Assert.Equal(id, await sut.EnqueueAsync(Req(key: "k1"), Ct));
        }

        await Assert.ThrowsAsync<OwnerApprovalRateLimitedException>(() => sut.EnqueueAsync(Req(key: "k2"), Ct));
    }

    [Fact]
    public async Task Enqueued_and_dispatched_requests_share_one_budget_per_agent()
    {
        var store = new InMemoryApprovalStore();
        var buckets = new InMemoryRateBucketStore();
        var options = new ApprovalOptions(RequestCapPerAgentHour: 2);
        var service = new ApprovalService(store, new InMemoryOwnerKeyRegistry(), buckets, options);
        var sut = new OwnerApprovalEnqueuer(store, buckets, options, new FakeTimeProvider(ApprovalTestData.Now));

        var dispatched = await service.RequestAsync(ApprovalTestData.Context(), Ct);
        Assert.Equal(ApprovalService.RequestOutcome.Created, dispatched.Outcome);
        var agent = dispatched.Request!.AgentId;
        await sut.EnqueueAsync(Req(agent: agent, key: "k1"), Ct);

        await Assert.ThrowsAsync<OwnerApprovalRateLimitedException>(() => sut.EnqueueAsync(Req(agent: agent, key: "k2"), Ct));
        var blocked = await service.RequestAsync(ApprovalTestData.Context(), Ct);
        Assert.Equal(ApprovalService.RequestOutcome.RateLimited, blocked.Outcome);
    }

    [Fact]
    public async Task Concurrent_distinct_keys_never_exceed_the_cap()
    {
        var store = new InMemoryApprovalStore();
        var sut = new OwnerApprovalEnqueuer(store, new InMemoryRateBucketStore(), new ApprovalOptions(RequestCapPerAgentHour: 5));

        var results = await Task.WhenAll(Enumerable.Range(0, 40).Select(i => Task.Run(async () =>
        {
            try
            {
                await sut.EnqueueAsync(Req(key: $"k{i}"), Ct);
                return true;
            }
            catch (OwnerApprovalRateLimitedException)
            {
                return false;
            }
        }, Ct)));

        Assert.Equal(5, results.Count(r => r));
        Assert.Equal(5, store.Count);
    }

    private sealed class FakeEnqueuer : IOwnerApprovalEnqueuer
    {
        public Task<Guid> EnqueueAsync(OwnerApprovalEnqueueRequest request, CancellationToken ct = default) => Task.FromResult(Guid.Empty);
    }

    /// <summary>Inserts the row through the inner store first, then throws, as a lost primary-key race would.</summary>
    private sealed class RacingStore(InMemoryApprovalStore inner) : IApprovalStore
    {
        private bool _first = true;

        public async Task InsertAsync(ApprovalRequest request, CancellationToken ct = default)
        {
            if (_first)
            {
                _first = false;
                await inner.InsertAsync(request, ct);
                throw new InvalidOperationException("duplicate key");
            }

            await inner.InsertAsync(request, ct);
        }

        public Task<ApprovalRequest?> GetAsync(Guid id, CancellationToken ct = default) =>
            _first ? Task.FromResult<ApprovalRequest?>(null) : inner.GetAsync(id, ct);

        public Task<bool> TryReplaceAsync(Guid id, ApprovalStatus expected, ApprovalRequest updated, CancellationToken ct = default) =>
            inner.TryReplaceAsync(id, expected, updated, ct);
    }

    private sealed class FailingStore : IApprovalStore
    {
        public Task InsertAsync(ApprovalRequest request, CancellationToken ct = default) => throw new TimeoutException();

        public Task<ApprovalRequest?> GetAsync(Guid id, CancellationToken ct = default) => Task.FromResult<ApprovalRequest?>(null);

        public Task<bool> TryReplaceAsync(Guid id, ApprovalStatus expected, ApprovalRequest updated, CancellationToken ct = default) =>
            Task.FromResult(false);
    }
}
