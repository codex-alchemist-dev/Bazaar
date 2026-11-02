using Bazaar.Server.Identity;
using Bazaar.Server.Identity.Leases;
using Bazaar.Server.Security.Policy;
using Bazaar.Server.Security.Taint;
using Microsoft.Extensions.Time.Testing;

namespace Bazaar.Server.Tests.Identity.Leases;

public sealed class IdentityServiceTests
{
    private const string _agent = "agent-a";
    private const string _otherAgent = "agent-b";
    private const string _service = "github";

    private static readonly DateTimeOffset _start = new(2026, 10, 30, 9, 0, 0, TimeSpan.Zero);

    private static AccessRequest Read(string agent = _agent) =>
        new(agent, _service, "read_issues", "triage");

    private static AccessRequest Write(string agent = _agent) =>
        new(agent, _service, "write_comment", "reply");

    private static Harness Build(Func<AccessRequest, ToolTier>? tierFor = null, IIdentityAudit? audit = null)
    {
        var time = new FakeTimeProvider(_start);
        var agents = new FakeAgentState();
        var directory = new InMemoryCredentialDirectory();
        directory.Add(_agent, _service, new CredentialId(Guid.CreateVersion7()));
        var leases = new InMemoryCredentialLeaseStore(time);
        var recorder = new RecordingAudit();
        var approvals = new FakeApprovals();
        var service = new IdentityService(
            new IdentityOptions(),
            agents,
            directory,
            leases,
            audit ?? recorder,
            approvals,
            time,
            tierFor);
        return new Harness(service, agents, leases, recorder, approvals, time);
    }

    [Fact]
    public async Task ThrowingAudit_Denies_AndIssuesNoLease()
    {
        var harness = Build(audit: new ThrowingAudit());

        var decision = await harness.Service.RequestAccessAsync(Read(), TestContext.Current.CancellationToken);

        Assert.IsType<Denied>(decision);
        Assert.Empty(await harness.Leases.ListActiveAsync(_agent, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task AuditIsWrittenBeforeLeaseIsIssued()
    {
        var harness = Build();

        var decision = await harness.Service.RequestAccessAsync(Read(), TestContext.Current.CancellationToken);

        var granted = Assert.IsType<Granted>(decision);
        Assert.Single(harness.Audit.Entries);
        Assert.Equal("lease.issued", harness.Audit.Entries[0].Kind);
        Assert.Contains(granted.Lease.Value.ToString(), harness.Audit.Entries[0].Payload);
        Assert.DoesNotContain("secret", harness.Audit.Entries[0].Payload, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task QuarantinedAgent_IsDenied_AndItsLeasesAreRevoked()
    {
        var harness = Build();
        var granted = Assert.IsType<Granted>(await harness.Service.RequestAccessAsync(Read(), TestContext.Current.CancellationToken));

        harness.Agents.Quarantine(_agent);
        var decision = await harness.Service.RequestAccessAsync(Read(), TestContext.Current.CancellationToken);

        Assert.IsType<Denied>(decision);
        Assert.Null(await harness.Leases.GetAsync(granted.Lease, _agent, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task MissingAccount_ReturnsHumanRequired()
    {
        var harness = Build();

        var decision = await harness.Service.RequestAccessAsync(Read(_otherAgent), TestContext.Current.CancellationToken);

        Assert.IsType<HumanRequired>(decision);
        Assert.Empty(harness.Audit.Entries);
    }

    [Fact]
    public async Task CookielessRequest_ForAnotherAgentsCredential_IsRefused()
    {
        var harness = Build();
        var granted = Assert.IsType<Granted>(await harness.Service.RequestAccessAsync(Read(), TestContext.Current.CancellationToken));

        // The lease belongs to _agent; a lookup naming another agent finds nothing.
        Assert.Null(await harness.Leases.GetAsync(granted.Lease, _otherAgent, TestContext.Current.CancellationToken));
        Assert.IsType<HumanRequired>(await harness.Service.RequestAccessAsync(Read(_otherAgent), TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task HardTaint_Tier2_IsDenied_EvenWithAnApproval()
    {
        var harness = Build();
        harness.Agents.SetTaint(_agent, TaintState.Hard);
        harness.Approvals.Grant(Guid.CreateVersion7());

        var decision = await harness.Service.RequestAccessAsync(Write(), TestContext.Current.CancellationToken);

        Assert.IsType<Denied>(decision);
        Assert.Empty(harness.Audit.Entries);
    }

    [Fact]
    public async Task HardTaint_EscalatesTier1ToTier2_AndDenies()
    {
        var harness = Build();
        harness.Agents.SetTaint(_agent, TaintState.Hard);

        var decision = await harness.Service.RequestAccessAsync(Read(), TestContext.Current.CancellationToken);

        Assert.IsType<Denied>(decision);
    }

    [Fact]
    public async Task SoftTaint_DoesNotBlockTier2_ItStillNeedsApproval()
    {
        var harness = Build();
        harness.Agents.SetTaint(_agent, TaintState.Soft);

        var decision = await harness.Service.RequestAccessAsync(Write(), TestContext.Current.CancellationToken);

        Assert.IsType<NeedsApproval>(decision);
    }

    [Fact]
    public async Task Tier2_WithoutApproval_NeedsApproval()
    {
        var harness = Build();

        var decision = await harness.Service.RequestAccessAsync(Write(), TestContext.Current.CancellationToken);

        var needs = Assert.IsType<NeedsApproval>(decision);
        Assert.Equal(ToolTier.OwnerApproves, needs.Tier);
        Assert.Empty(harness.Audit.Entries);
    }

    [Fact]
    public async Task Tier2_WithApproval_IsGrantedOnce_AndTheApprovalIsSingleUse()
    {
        var harness = Build();
        var approval = Guid.CreateVersion7();
        harness.Approvals.Grant(approval);

        var first = await harness.Service.RequestAccessAsync(Write(), TestContext.Current.CancellationToken);
        Assert.IsType<Granted>(first);

        // The same approval cannot back a second lease.
        harness.Approvals.Grant(approval);
        var second = await harness.Service.RequestAccessAsync(Write(), TestContext.Current.CancellationToken);
        Assert.IsType<Denied>(second);
        Assert.Single(await harness.Leases.ListActiveAsync(_agent, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Grant_ExpiresAfterTheTtl()
    {
        var harness = Build();
        var granted = Assert.IsType<Granted>(await harness.Service.RequestAccessAsync(Read(), TestContext.Current.CancellationToken));

        Assert.Equal(_start.AddMinutes(15), granted.ExpiresAt);
        Assert.NotNull(await harness.Leases.GetAsync(granted.Lease, _agent, TestContext.Current.CancellationToken));

        harness.Time.Advance(TimeSpan.FromMinutes(15));

        Assert.Null(await harness.Leases.GetAsync(granted.Lease, _agent, TestContext.Current.CancellationToken));
        Assert.Empty(await harness.Leases.ListActiveAsync(_agent, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task RevokeAgent_RemovesItsLeases_AndLeavesOthers()
    {
        var harness = Build();
        var mine = Assert.IsType<Granted>(await harness.Service.RequestAccessAsync(Read(), TestContext.Current.CancellationToken));

        await harness.Service.RevokeAsync(new AgentRevocation(_agent), TestContext.Current.CancellationToken);

        Assert.Null(await harness.Leases.GetAsync(mine.Lease, _agent, TestContext.Current.CancellationToken));
        Assert.Empty(await harness.Leases.ListActiveAsync(_agent, TestContext.Current.CancellationToken));
        Assert.Equal("lease.revoked", harness.Audit.Entries[^1].Kind);
    }

    [Fact]
    public async Task GlobalRevocation_RemovesEveryAgentsLeases()
    {
        var harness = Build();
        var mine = Assert.IsType<Granted>(await harness.Service.RequestAccessAsync(Read(), TestContext.Current.CancellationToken));

        await harness.Service.RevokeAsync(new GlobalRevocation(), TestContext.Current.CancellationToken);

        Assert.Null(await harness.Leases.GetAsync(mine.Lease, _agent, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task DeniedTier_IsDenied()
    {
        var harness = Build(tierFor: _ => ToolTier.Denied);

        Assert.IsType<Denied>(await harness.Service.RequestAccessAsync(Read(), TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task HumanPerformsTier_ReturnsHumanRequired()
    {
        var harness = Build(tierFor: _ => ToolTier.HumanPerforms);

        Assert.IsType<HumanRequired>(await harness.Service.RequestAccessAsync(Read(), TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task DefaultTierMapping_TreatsWriteAndSpendAsTier2()
    {
        Assert.Equal(ToolTier.OwnerApproves, IdentityService.DefaultTierFor(new AccessRequest(_agent, _service, "Spend_credits", "x")));
        Assert.Equal(ToolTier.AutonomousWithNotice, IdentityService.DefaultTierFor(Read()));

        var harness = Build();
        Assert.IsType<Granted>(await harness.Service.RequestAccessAsync(Read(), TestContext.Current.CancellationToken));
    }

    private sealed record Harness(
        IdentityService Service,
        FakeAgentState Agents,
        InMemoryCredentialLeaseStore Leases,
        RecordingAudit Audit,
        FakeApprovals Approvals,
        FakeTimeProvider Time);

    private sealed class FakeAgentState : IAgentStateReader
    {
        private readonly Dictionary<string, TaintState> _taint = new(StringComparer.Ordinal);
        private readonly HashSet<string> _quarantined = new(StringComparer.Ordinal);

        public void SetTaint(string agentId, TaintState state) => _taint[agentId] = state;

        public void Quarantine(string agentId) => _quarantined.Add(agentId);

        public SessionTaint TaintFor(string agentId) =>
            new(_taint.GetValueOrDefault(agentId, TaintState.Clean), []);

        public bool IsQuarantined(string agentId) => _quarantined.Contains(agentId);
    }

    private sealed class RecordingAudit : IIdentityAudit
    {
        public List<(string Kind, string Payload)> Entries { get; } = [];

        public Task AppendAsync(string kind, string payloadJson, CancellationToken ct)
        {
            Entries.Add((kind, payloadJson));
            return Task.CompletedTask;
        }
    }

    private sealed class ThrowingAudit : IIdentityAudit
    {
        public Task AppendAsync(string kind, string payloadJson, CancellationToken ct) =>
            throw new InvalidOperationException("audit store unavailable");
    }

    private sealed class FakeApprovals : IAccessApprovalSource
    {
        private Guid? _next;

        public void Grant(Guid approvalId) => _next = approvalId;

        public Task<Guid?> FindApprovedAsync(AccessRequest request, CancellationToken ct = default)
        {
            var value = _next;
            _next = null;
            return Task.FromResult(value);
        }
    }
}
