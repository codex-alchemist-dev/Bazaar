using System.Security.Cryptography;
using Bazaar.Approvals;
using Bazaar.Server.Security.Approvals;
using Microsoft.Extensions.Time.Testing;

namespace Bazaar.Server.VmEgress.Approvals.Security.Tests;

public sealed class ApprovalServiceEgressApprovalPortTests : IDisposable
{
    private const string _device = "device-1";
    private static readonly string[] _adds = ["a.example", "b.example"];

    private readonly ECDsa _key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
    private readonly InMemoryApprovalStore _store = new();
    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 11, 22, 6, 0, 0, TimeSpan.Zero));
    private readonly ApprovalService _service;
    private readonly ApprovalServiceEgressApprovalPort _port;

    public ApprovalServiceEgressApprovalPortTests()
    {
        var keys = new InMemoryOwnerKeyRegistry();
        keys.Register(new OwnerDeviceKey("owner", _device, _key.ExportSubjectPublicKeyInfo()));
        _service = new ApprovalService(_store, keys, new Bazaar.Server.Security.Policy.InMemoryRateBucketStore(), new ApprovalOptions());
        _port = new ApprovalServiceEgressApprovalPort(_service, _store, _time);
    }

    public void Dispose()
    {
        _key.Dispose();
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static EgressApprovalRequest Request(string agent = "agent-1", string key = "key-1", int tier = 2) =>
        new(agent, _adds, key, tier);

    private async Task DecideAsync(Guid id, ApprovalDecision decision)
    {
        var row = (await _store.GetAsync(id, Ct))!;
        var signature = _key.SignData(
            ApprovalSignature.Message(id, decision, row.ArgsHash, row.ExpiresAt, _device),
            HashAlgorithmName.SHA256,
            DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
        var result = await _service.DecideAsync(id, decision, _device, signature, _time.GetUtcNow(), Ct);
        Assert.Equal(ApprovalService.DecisionOutcome.Recorded, result.Outcome);
    }

    [Fact]
    public async Task Request_files_a_pending_egress_card_that_the_owner_can_approve()
    {
        var ticket = await _port.RequestOrGetAsync(Request(), Ct);

        Assert.Equal(EgressTicketStatus.Pending, ticket.Status);
        var row = (await _store.GetAsync(ticket.Id, Ct))!;
        Assert.Equal("vm.egress.allowlist_change", row.Tool);
        Assert.Equal("agent-1", row.AgentId);
        Assert.Equal("key-1", row.Target);
        Assert.Equal(ApprovalServiceEgressApprovalPort.SessionIdFor("agent-1"), row.SessionId);
        Assert.Equal("""{"agentId":"agent-1","additions":["a.example","b.example"],"additionsKey":"key-1"}""", row.PayloadJson);

        await DecideAsync(ticket.Id, ApprovalDecision.Approve);
        var after = await _port.RequestOrGetAsync(Request(), Ct);

        Assert.Equal(ticket.Id, after.Id);
        Assert.Equal(EgressTicketStatus.Approved, after.Status);
    }

    [Fact]
    public async Task Repeated_request_reuses_the_same_card()
    {
        var first = await _port.RequestOrGetAsync(Request(), Ct);
        var second = await _port.RequestOrGetAsync(Request(), Ct);

        Assert.Equal(first.Id, second.Id);
        Assert.Equal(EgressTicketStatus.Pending, second.Status);
    }

    [Fact]
    public async Task Ticket_echoes_the_additions_key()
    {
        var ticket = await _port.RequestOrGetAsync(Request(key: "key-xyz"), Ct);

        Assert.Equal("key-xyz", ticket.AdditionsKey);
    }

    [Fact]
    public async Task Denied_is_reported_once_then_a_fresh_card_is_filed()
    {
        var first = await _port.RequestOrGetAsync(Request(), Ct);
        await DecideAsync(first.Id, ApprovalDecision.Deny);

        var denied = await _port.RequestOrGetAsync(Request(), Ct);
        var fresh = await _port.RequestOrGetAsync(Request(), Ct);

        Assert.Equal(EgressTicketStatus.Denied, denied.Status);
        Assert.Equal(first.Id, denied.Id);
        Assert.Equal(EgressTicketStatus.Pending, fresh.Status);
        Assert.NotEqual(first.Id, fresh.Id);
    }

    [Fact]
    public async Task Expired_is_reported_once_then_a_fresh_card_is_filed()
    {
        var first = await _port.RequestOrGetAsync(Request(), Ct);
        _time.Advance(TimeSpan.FromHours(25));

        var expired = await _port.RequestOrGetAsync(Request(), Ct);
        var fresh = await _port.RequestOrGetAsync(Request(), Ct);

        Assert.Equal(EgressTicketStatus.Expired, expired.Status);
        Assert.Equal(first.Id, expired.Id);
        Assert.Equal(EgressTicketStatus.Pending, fresh.Status);
        Assert.NotEqual(first.Id, fresh.Id);
    }

    [Fact]
    public async Task Consumed_ticket_is_retired_and_the_next_request_files_fresh()
    {
        var first = await _port.RequestOrGetAsync(Request(), Ct);
        await DecideAsync(first.Id, ApprovalDecision.Approve);
        Assert.True(await _port.ConsumeAsync(first.Id, Ct));

        var next = await _port.RequestOrGetAsync(Request(), Ct);

        Assert.Equal(EgressTicketStatus.Pending, next.Status);
        Assert.NotEqual(first.Id, next.Id);
    }

    [Fact]
    public async Task Consume_has_a_single_winner_under_concurrency()
    {
        var ticket = await _port.RequestOrGetAsync(Request(), Ct);
        await DecideAsync(ticket.Id, ApprovalDecision.Approve);

        var results = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => Task.Run(() => _port.ConsumeAsync(ticket.Id, Ct), Ct)));

        Assert.Equal(1, results.Count(r => r));
    }

    [Fact]
    public async Task Consume_refuses_a_pending_ticket()
    {
        var ticket = await _port.RequestOrGetAsync(Request(), Ct);

        Assert.False(await _port.ConsumeAsync(ticket.Id, Ct));
    }

    [Fact]
    public async Task Consume_refuses_an_unknown_id()
    {
        Assert.False(await _port.ConsumeAsync(Guid.NewGuid(), Ct));
    }

    [Fact]
    public async Task Consume_refuses_an_approved_row_for_another_tool()
    {
        var other = await FileOtherToolAsync();
        await DecideAsync(other, ApprovalDecision.Approve);

        Assert.False(await _port.ConsumeAsync(other, Ct));
        Assert.Equal(ApprovalStatus.Approved, (await _store.GetAsync(other, Ct))!.Status);
    }

    [Fact]
    public async Task Rate_limited_request_throws()
    {
        var limited = new ApprovalService(_store, new InMemoryOwnerKeyRegistry(), new Bazaar.Server.Security.Policy.InMemoryRateBucketStore(), new ApprovalOptions(RequestCapPerAgentHour: 1));
        var port = new ApprovalServiceEgressApprovalPort(limited, _store, _time);
        await port.RequestOrGetAsync(Request(key: "k1"), Ct);

        await Assert.ThrowsAsync<InvalidOperationException>(() => port.RequestOrGetAsync(Request(key: "k2"), Ct));
    }

    [Fact]
    public async Task Tier_below_the_floor_throws()
    {
        await Assert.ThrowsAsync<InvalidOperationException>(() => _port.RequestOrGetAsync(Request(tier: 1), Ct));
    }

    [Fact]
    public async Task Different_additions_keys_get_different_cards()
    {
        var a = await _port.RequestOrGetAsync(Request(key: "k1"), Ct);
        var b = await _port.RequestOrGetAsync(Request(key: "k2"), Ct);

        Assert.NotEqual(a.Id, b.Id);
    }

    [Fact]
    public void Session_id_is_stable_per_agent_and_differs_between_agents()
    {
        Assert.Equal(ApprovalServiceEgressApprovalPort.SessionIdFor("a"), ApprovalServiceEgressApprovalPort.SessionIdFor("a"));
        Assert.NotEqual(ApprovalServiceEgressApprovalPort.SessionIdFor("a"), ApprovalServiceEgressApprovalPort.SessionIdFor("b"));
    }

    private async Task<Guid> FileOtherToolAsync()
    {
        var context = new Bazaar.Server.Security.Policy.DispatchContext(
            "agent-1",
            Guid.NewGuid(),
            Bazaar.Server.Security.Policy.SessionRoles.Actor,
            "other.tool",
            null,
            null,
            System.Text.Json.JsonElement.Parse("{}"),
            Bazaar.Server.Security.Taint.SessionTaint.Clean,
            _time.GetUtcNow());
        var result = await _service.RequestAsync(context, Ct);
        return result.Request!.Id;
    }
}
