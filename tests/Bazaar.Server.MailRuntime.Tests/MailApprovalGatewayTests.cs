using Bazaar.Server.ApprovalQueue;
using Bazaar.Server.MailOutbound;
using Microsoft.Extensions.DependencyInjection;

namespace Bazaar.Server.MailRuntime.Tests;

public sealed class MailApprovalGatewayTests
{
    private sealed class FakeQueue : IApprovalQueue
    {
        public readonly Dictionary<Guid, ApprovalItem> Items = [];
        public NewApproval? Last;
        public bool Refuse;

        public Task<Guid> RequestAsync(NewApproval request, CancellationToken ct = default)
        {
            if (Refuse) throw new ApprovalRefusedException(ApprovalRefusal.InvalidRequest, "refused");
            Last = request;
            var id = Guid.NewGuid();
            Items[id] = Item(id, ApprovalStatus.Pending);
            return Task.FromResult(id);
        }

        public Task<ApprovalItem?> GetAsync(Guid id, CancellationToken ct = default) => Task.FromResult(Items.GetValueOrDefault(id));

        public Task<IReadOnlyList<ApprovalItem>> ListPendingAsync(string? kind, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<ApprovalItem>>([.. Items.Values.Where(i => i.Status == ApprovalStatus.Pending)]);

        public Task<ApprovalItem> DecideAsync(Guid id, Guid approverPrincipalId, bool approve, string? reason, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task<bool> TryConsumeAsync(Guid id, CancellationToken ct = default)
        {
            if (!Items.TryGetValue(id, out var item) || item.Status != ApprovalStatus.Approved) return Task.FromResult(false);
            Items[id] = item with { Status = ApprovalStatus.Consumed };
            return Task.FromResult(true);
        }

        public void Set(Guid id, ApprovalStatus status) => Items[id] = Items[id] with { Status = status };

        private static ApprovalItem Item(Guid id, ApprovalStatus status) =>
            new(id, "mail.outbound", 2, "s", Guid.NewGuid(), null, status, DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch, null, null, null);
    }

    private static MailApprovalRequest Request(string agent = "agent-one") => new(
        agent, "key-123", 2, default, new Bazaar.Server.Mail.MailboxRef("mail.example.com", "writer"),
        ["Pat.Doe@Example.org", "lee@example.org", "sam@example.net"], "Private subject line", "the body text here", "footer", 1);

    private static MailApprovalGateway Gateway(FakeQueue q, int ttlHours = 24) =>
        new(q, new StaticMailRuntimeOptionsSource(new MailRuntimeOptions { ApprovalTtlHours = ttlHours }));

    [Fact]
    public async Task Request_maps_fields_and_never_carries_body_subject_or_local_parts()
    {
        var q = new FakeQueue();
        var id = await Gateway(q).RequestAsync(Request(), CancellationToken.None);

        Assert.True(q.Items.ContainsKey(id));
        var sent = q.Last!;
        Assert.Equal("mail.outbound", sent.Kind);
        Assert.Equal(2, sent.Tier);
        Assert.Equal("key-123", sent.PayloadRef);
        Assert.Equal(TimeSpan.FromHours(24), sent.Ttl);
        Assert.Contains("example.org", sent.Subject);
        Assert.Contains("example.net", sent.Subject);
        Assert.DoesNotContain("body text", sent.Subject);
        Assert.DoesNotContain("Private subject", sent.Subject);
        Assert.DoesNotContain("pat.doe", sent.Subject, StringComparison.OrdinalIgnoreCase);
        Assert.True(sent.Subject.Length <= 200);
    }

    [Fact]
    public async Task Requester_principal_is_stable_per_agent()
    {
        var q = new FakeQueue();
        var g = Gateway(q);
        await g.RequestAsync(Request("agent-one"), CancellationToken.None);
        var a = q.Last!.RequesterPrincipalId;
        await g.RequestAsync(Request("agent-one"), CancellationToken.None);
        Assert.Equal(a, q.Last.RequesterPrincipalId);
        await g.RequestAsync(Request("agent-two"), CancellationToken.None);
        Assert.NotEqual(a, q.Last.RequesterPrincipalId);
        var guid = Guid.NewGuid();
        await g.RequestAsync(Request(guid.ToString()), CancellationToken.None);
        Assert.Equal(guid, q.Last.RequesterPrincipalId);
    }

    [Theory]
    [InlineData(ApprovalStatus.Pending, MailApprovalStatus.Pending)]
    [InlineData(ApprovalStatus.Approved, MailApprovalStatus.Approved)]
    [InlineData(ApprovalStatus.Denied, MailApprovalStatus.Denied)]
    [InlineData(ApprovalStatus.Expired, MailApprovalStatus.Expired)]
    [InlineData(ApprovalStatus.Consumed, MailApprovalStatus.Consumed)]
    public async Task Status_maps(ApprovalStatus from, MailApprovalStatus expected)
    {
        var q = new FakeQueue();
        var g = Gateway(q);
        var id = await g.RequestAsync(Request(), CancellationToken.None);
        q.Set(id, from);
        Assert.Equal(expected, await g.GetStatusAsync(id, CancellationToken.None));
    }

    [Fact]
    public async Task Unknown_id_never_reads_as_approved() =>
        Assert.Equal(MailApprovalStatus.Expired, await Gateway(new FakeQueue()).GetStatusAsync(Guid.NewGuid(), CancellationToken.None));

    [Fact]
    public async Task Consume_is_single_use_and_needs_an_approval()
    {
        var q = new FakeQueue();
        var g = Gateway(q);
        var id = await g.RequestAsync(Request(), CancellationToken.None);
        Assert.False(await g.ConsumeAsync(id, CancellationToken.None));
        q.Set(id, ApprovalStatus.Approved);
        Assert.True(await g.ConsumeAsync(id, CancellationToken.None));
        Assert.False(await g.ConsumeAsync(id, CancellationToken.None));
        Assert.Equal(MailApprovalStatus.Consumed, await g.GetStatusAsync(id, CancellationToken.None));
    }

    [Fact]
    public async Task Refusal_surfaces_as_a_documented_exception_with_the_reason()
    {
        var q = new FakeQueue { Refuse = true };
        var ex = await Assert.ThrowsAsync<MailApprovalRefusedException>(() => Gateway(q).RequestAsync(Request(), CancellationToken.None));
        Assert.Equal(ApprovalRefusal.InvalidRequest, ex.Refusal);
    }

    [Theory]
    [InlineData(0, 24)]
    [InlineData(-3, 24)]
    [InlineData(1, 1)]
    [InlineData(48, 48)]
    [InlineData(100000, 720)]
    public async Task Ttl_is_bounded(int setting, int expectedHours)
    {
        var q = new FakeQueue();
        await Gateway(q, setting).RequestAsync(Request(), CancellationToken.None);
        Assert.Equal(TimeSpan.FromHours(expectedHours), q.Last!.Ttl);
    }

    [Fact]
    public void Runtime_uses_the_adapter_only_when_a_queue_is_registered()
    {
        var withQueue = new ServiceCollection();
        withQueue.AddSingleton<IApprovalQueue>(new FakeQueue());
        withQueue.AddBazaarMailRuntime();
        using (var sp = withQueue.BuildServiceProvider())
            Assert.IsType<MailApprovalGateway>(sp.GetRequiredService<IMailApprovalGateway>());

        var without = new ServiceCollection();
        without.AddBazaarMailRuntime();
        using (var sp = without.BuildServiceProvider())
            Assert.IsType<FailClosedMailApprovalGateway>(sp.GetRequiredService<IMailApprovalGateway>());
    }

    [Fact]
    public void A_gateway_the_host_registered_first_wins()
    {
        var services = new ServiceCollection();
        var own = new FailClosedMailApprovalGateway();
        services.AddSingleton<IMailApprovalGateway>(own);
        services.AddSingleton<IApprovalQueue>(new FakeQueue());
        services.AddBazaarMailRuntime();
        using var sp = services.BuildServiceProvider();
        Assert.Same(own, sp.GetRequiredService<IMailApprovalGateway>());
    }
}
