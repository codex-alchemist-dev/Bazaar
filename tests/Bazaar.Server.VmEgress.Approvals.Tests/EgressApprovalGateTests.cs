using System.Text.Json;
using Bazaar.Server.Security.Audit;
using Bazaar.Server.VmEgress.Approvals;
using Microsoft.Extensions.DependencyInjection;

namespace Bazaar.Server.VmEgress.Approvals.Tests;

public sealed class EgressApprovalGateTests
{
    private static readonly string[] _adds = ["b.example.com:443", "a.example.com:443"];

    [Fact]
    public async Task First_call_files_a_request_and_is_pending()
    {
        var h = new Harness();
        var state = await h.Gate.RequestGrowthAsync("agent-1", _adds, 2, TestContext.Current.CancellationToken);

        Assert.Equal(EgressApprovalState.Pending, state);
        Assert.Single(h.Port.Requests);
        Assert.Equal("Pending", h.Decisions().Single().GetProperty("decision").GetString());
    }

    [Fact]
    public async Task Approved_is_returned_once_and_the_grant_is_used_up()
    {
        var h = new Harness();
        await h.Gate.RequestGrowthAsync("agent-1", _adds, 2, TestContext.Current.CancellationToken);
        h.Port.Status = EgressTicketStatus.Approved;

        var first = await h.Gate.RequestGrowthAsync("agent-1", _adds, 2, TestContext.Current.CancellationToken);
        var second = await h.Gate.RequestGrowthAsync("agent-1", _adds, 2, TestContext.Current.CancellationToken);

        Assert.Equal(EgressApprovalState.Approved, first);
        Assert.Equal(EgressApprovalState.Denied, second);
        Assert.Equal("grant-already-used", h.Decisions()[^1].GetProperty("reason").GetString());
    }

    [Fact]
    public async Task Additions_are_canonical_so_order_and_duplicates_do_not_matter()
    {
        var h = new Harness();
        await h.Gate.RequestGrowthAsync("agent-1", ["b", "a", "a"], 2, TestContext.Current.CancellationToken);
        await h.Gate.RequestGrowthAsync("agent-1", ["a", "b"], 2, TestContext.Current.CancellationToken);

        Assert.Equal(h.Port.Requests[0].AdditionsKey, h.Port.Requests[1].AdditionsKey);
        Assert.Equal(["a", "b"], h.Port.Requests[0].Additions);
    }

    [Fact]
    public async Task Different_additions_get_a_different_key()
    {
        var h = new Harness();
        await h.Gate.RequestGrowthAsync("agent-1", ["a"], 2, TestContext.Current.CancellationToken);
        await h.Gate.RequestGrowthAsync("agent-1", ["a", "b"], 2, TestContext.Current.CancellationToken);

        Assert.NotEqual(h.Port.Requests[0].AdditionsKey, h.Port.Requests[1].AdditionsKey);
    }

    [Fact]
    public async Task Ticket_for_other_additions_is_never_approved()
    {
        var h = new Harness { };
        h.Port.Status = EgressTicketStatus.Approved;
        h.Port.KeyOverride = "something-else";

        var state = await h.Gate.RequestGrowthAsync("agent-1", _adds, 2, TestContext.Current.CancellationToken);

        Assert.Equal(EgressApprovalState.Denied, state);
        Assert.Equal(0, h.Port.Consumed);
        Assert.Equal("ticket-mismatch", h.Decisions().Single().GetProperty("reason").GetString());
    }

    [Theory]
    [InlineData(EgressTicketStatus.Denied, "owner-denied")]
    [InlineData(EgressTicketStatus.Expired, "expired")]
    public async Task Refused_or_expired_ticket_is_denied(EgressTicketStatus status, string reason)
    {
        var h = new Harness();
        h.Port.Status = status;

        var state = await h.Gate.RequestGrowthAsync("agent-1", _adds, 2, TestContext.Current.CancellationToken);

        Assert.Equal(EgressApprovalState.Denied, state);
        Assert.Equal(reason, h.Decisions().Single().GetProperty("reason").GetString());
    }

    [Fact]
    public async Task Hard_taint_is_refused_without_asking_the_owner()
    {
        var h = new Harness();
        h.Taint.Tainted = true;
        h.Port.Status = EgressTicketStatus.Approved;

        var state = await h.Gate.RequestGrowthAsync("agent-1", _adds, 2, TestContext.Current.CancellationToken);

        Assert.Equal(EgressApprovalState.Denied, state);
        Assert.Empty(h.Port.Requests);
        Assert.Equal("hard-tainted", h.Decisions().Single().GetProperty("reason").GetString());
    }

    [Fact]
    public async Task Taint_reader_failure_denies()
    {
        var h = new Harness();
        h.Taint.Fail = true;

        var state = await h.Gate.RequestGrowthAsync("agent-1", _adds, 2, TestContext.Current.CancellationToken);

        Assert.Equal(EgressApprovalState.Denied, state);
        Assert.Empty(h.Port.Requests);
        Assert.Equal("taint-check-failed", h.Decisions().Single().GetProperty("reason").GetString());
    }

    [Fact]
    public async Task Approvals_service_failure_denies()
    {
        var h = new Harness();
        h.Port.Fail = true;

        var state = await h.Gate.RequestGrowthAsync("agent-1", _adds, 2, TestContext.Current.CancellationToken);

        Assert.Equal(EgressApprovalState.Denied, state);
        Assert.Equal("approvals-unavailable", h.Decisions().Single().GetProperty("reason").GetString());
    }

    [Fact]
    public async Task Consume_failure_denies()
    {
        var h = new Harness();
        h.Port.Status = EgressTicketStatus.Approved;
        h.Port.FailConsume = true;

        var state = await h.Gate.RequestGrowthAsync("agent-1", _adds, 2, TestContext.Current.CancellationToken);

        Assert.Equal(EgressApprovalState.Denied, state);
        Assert.Equal("consume-failed", h.Decisions().Single().GetProperty("reason").GetString());
    }

    [Fact]
    public async Task Slow_approvals_service_times_out_and_denies_even_if_it_ignores_its_token()
    {
        var h = new Harness(TimeSpan.FromMilliseconds(50));
        h.Port.Hang = true;

        var state = await h.Gate.RequestGrowthAsync("agent-1", _adds, 2, TestContext.Current.CancellationToken);

        Assert.Equal(EgressApprovalState.Denied, state);
        Assert.Equal("approvals-unavailable", h.Decisions().Single().GetProperty("reason").GetString());
    }

    [Fact]
    public async Task Cancelled_caller_gets_the_cancellation_and_no_grant_is_spent()
    {
        var h = new Harness();
        h.Port.Status = EgressTicketStatus.Approved;
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => h.Gate.RequestGrowthAsync("agent-1", _adds, 2, cts.Token));

        Assert.Equal(0, h.Port.Consumed);
        Assert.Empty(h.Audit.Entries);
    }

    [Fact]
    public async Task Cancellation_during_the_port_call_reaches_the_caller()
    {
        var h = new Harness();
        h.Port.Hang = true;
        using var cts = new CancellationTokenSource();

        var task = h.Gate.RequestGrowthAsync("agent-1", _adds, 2, cts.Token);
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => task);
        Assert.Equal(0, h.Port.Consumed);
    }

    [Fact]
    public async Task Every_decision_writes_exactly_one_audit_row_with_the_tier_floor_applied()
    {
        var h = new Harness();
        await h.Gate.RequestGrowthAsync("agent-1", _adds, 0, TestContext.Current.CancellationToken);
        h.Port.Status = EgressTicketStatus.Approved;
        await h.Gate.RequestGrowthAsync("agent-1", _adds, 3, TestContext.Current.CancellationToken);
        h.Taint.Tainted = true;
        await h.Gate.RequestGrowthAsync("agent-1", _adds, 2, TestContext.Current.CancellationToken);

        var rows = h.Decisions();
        Assert.Equal(3, rows.Count);
        Assert.Equal(["Pending", "Approved", "Denied"], rows.Select(r => r.GetProperty("decision").GetString()));
        Assert.Equal([2, 3, 2], rows.Select(r => r.GetProperty("tier").GetInt32()));
        Assert.All(h.Audit.Entries, e => Assert.Equal(EgressApprovalGate.AuditKind, e.Kind));
        Assert.Equal(2, h.Port.Requests[0].Tier);
    }

    [Fact]
    public async Task Approved_is_withheld_when_its_audit_row_cannot_be_written()
    {
        var h = new Harness();
        h.Port.Status = EgressTicketStatus.Approved;
        h.Audit.Fail = true;

        var state = await h.Gate.RequestGrowthAsync("agent-1", _adds, 2, TestContext.Current.CancellationToken);

        Assert.Equal(EgressApprovalState.Denied, state);
    }

    [Fact]
    public async Task Denial_stands_when_its_audit_row_cannot_be_written()
    {
        var h = new Harness();
        h.Taint.Tainted = true;
        h.Audit.Fail = true;

        var state = await h.Gate.RequestGrowthAsync("agent-1", _adds, 2, TestContext.Current.CancellationToken);

        Assert.Equal(EgressApprovalState.Denied, state);
    }

    [Fact]
    public async Task Empty_additions_are_denied()
    {
        var h = new Harness();

        var state = await h.Gate.RequestGrowthAsync("agent-1", [" "], 2, TestContext.Current.CancellationToken);

        Assert.Equal(EgressApprovalState.Denied, state);
        Assert.Empty(h.Port.Requests);
    }

    [Fact]
    public async Task Module_replaces_the_deny_all_gate_and_fails_closed_without_adapters()
    {
        var audit = new RecordingAudit();
        var services = new ServiceCollection();
        services.AddSingleton<IAuditLog>(audit);
        services.AddBazaarVmEgress();
        services.AddBazaarVmEgressApprovals();
        await using var sp = services.BuildServiceProvider();

        var gate = sp.GetRequiredService<IEgressApprovalGate>();
        var state = await gate.RequestGrowthAsync("agent-1", _adds, 2, TestContext.Current.CancellationToken);

        Assert.IsType<EgressApprovalGate>(gate);
        Assert.Equal(EgressApprovalState.Denied, state);
        Assert.Single(audit.Entries);
    }

    [Fact]
    public void Options_reject_a_non_positive_timeout()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new EgressApprovalGateOptions { PortTimeout = TimeSpan.Zero }.Validate());
    }

    private sealed class Harness
    {
        public Harness(TimeSpan? timeout = null)
        {
            Gate = new EgressApprovalGate(
                Port,
                Taint,
                Audit,
                new EgressApprovalGateOptions { PortTimeout = timeout ?? TimeSpan.FromSeconds(5) },
                TimeProvider.System);
        }

        public FakePort Port { get; } = new();

        public FakeTaint Taint { get; } = new();

        public RecordingAudit Audit { get; } = new();

        public EgressApprovalGate Gate { get; }

        public List<JsonElement> Decisions() => Audit.Entries.Select(e => JsonElement.Parse(e.Payload)).ToList();
    }

    private sealed class FakePort : IEgressApprovalPort
    {
        private bool _consumed;

        public List<EgressApprovalRequest> Requests { get; } = [];

        public EgressTicketStatus Status { get; set; } = EgressTicketStatus.Pending;

        public string? KeyOverride { get; set; }

        public bool Fail { get; set; }

        public bool FailConsume { get; set; }

        public bool Hang { get; set; }

        public int Consumed { get; private set; }

        public async Task<EgressApprovalTicket> RequestOrGetAsync(EgressApprovalRequest request, CancellationToken ct)
        {
            Requests.Add(request);
            if (Fail)
            {
                throw new InvalidOperationException("down");
            }

            if (Hang)
            {
                // Ignores its token on purpose, except for the caller's own cancellation in the test below.
                await Task.Delay(Timeout.InfiniteTimeSpan, CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(30), CancellationToken.None).ConfigureAwait(false);
            }

            return new EgressApprovalTicket(Guid.NewGuid(), KeyOverride ?? request.AdditionsKey, Status);
        }

        public Task<bool> ConsumeAsync(Guid ticketId, CancellationToken ct)
        {
            if (FailConsume)
            {
                throw new InvalidOperationException("down");
            }

            if (_consumed)
            {
                return Task.FromResult(false);
            }

            _consumed = true;
            Consumed++;
            return Task.FromResult(true);
        }
    }

    private sealed class FakeTaint : IEgressTaintReader
    {
        public bool Tainted { get; set; }

        public bool Fail { get; set; }

        public ValueTask<bool> IsHardTaintedAsync(string agentId, CancellationToken ct) =>
            Fail ? throw new InvalidOperationException("down") : ValueTask.FromResult(Tainted);
    }

    private sealed class RecordingAudit : IAuditLog
    {
        public List<AuditEntry> Entries { get; } = [];

        public bool Fail { get; set; }

        public Task AppendAsync(AuditEntry e, CancellationToken ct = default)
        {
            if (Fail)
            {
                throw new InvalidOperationException("audit down");
            }

            Entries.Add(e);
            return Task.CompletedTask;
        }
    }
}
