using Bazaar.Approvals;
using Bazaar.Server.CodingGit;
using Bazaar.Server.CodingPermissions;
using Bazaar.Server.Host.Coding;
using Bazaar.Server.Security.Approvals;
using Bazaar.Server.Security.Policy;

namespace Bazaar.Server.Host.Tests.Coding;

/// <summary>Filing the owner approval request for a push that needs one (doc 17, "Push approval filing").</summary>
public sealed class ApprovalFilingPushGateTests
{
    private static readonly CancellationToken _ct = TestContext.Current.CancellationToken;
    private static readonly PushGateRequest _request = new("agent-1", new RepoRef("r", "main"), BranchName.ForTask("fix"), "gc-1", "fix");

    [Fact]
    public async Task FiledRequestHasTheHashTheGrantLookupChecks()
    {
        var store = new InMemoryApprovalStore();
        var service = new ApprovalService(store, new InMemoryOwnerKeyRegistry(), new InMemoryRateBucketStore(), new ApprovalOptions());
        var session = Guid.NewGuid();
        var gate = new ApprovalFilingPushGate(new Fixed(PushDecision.NeedsApproval("tainted")), new Sessions(session), new ApprovalServicePushRequests(service, TimeProvider.System));

        var decision = await gate.EvaluateAsync(_request, _ct);

        Assert.Equal(PushVerdict.NeedsApproval, decision.Verdict);
        Assert.Contains("waiting for the owner", decision.Reason);
        var id = Guid.Parse(decision.Reason.Split(' ').Single(w => Guid.TryParse(w, out _)));
        var row = await store.GetAsync(id, _ct);
        Assert.NotNull(row);
        Assert.Equal(PermissionGatePushGate.DispatchHashOf(_request), row.ArgsHash);
        Assert.Equal(session, row.SessionId);
        Assert.Equal("agent-1", row.AgentId);
    }

    [Fact]
    public async Task OtherVerdictsAreLeftAlone()
    {
        var requests = new CountingRequests();
        var gate = new ApprovalFilingPushGate(new Fixed(PushDecision.Allow()), new Sessions(Guid.NewGuid()), requests);
        Assert.Equal(PushVerdict.Allow, (await gate.EvaluateAsync(_request, _ct)).Verdict);
        Assert.Equal(0, requests.Calls);
    }

    [Fact]
    public async Task FilingFailureKeepsTheVerdict()
    {
        var gate = new ApprovalFilingPushGate(new Fixed(PushDecision.NeedsApproval("tainted")), new Sessions(Guid.NewGuid()), new ThrowingRequests());
        var decision = await gate.EvaluateAsync(_request, _ct);
        Assert.Equal(PushVerdict.NeedsApproval, decision.Verdict);
        Assert.Equal("tainted", decision.Reason);
    }

    private sealed class Fixed(PushDecision decision) : IPushGate
    {
        public ValueTask<PushDecision> EvaluateAsync(PushGateRequest request, CancellationToken cancellationToken) => ValueTask.FromResult(decision);
    }

    private sealed class Sessions(Guid id) : IPushSessionRecords
    {
        public ValueTask<PushSessionRecord?> FindAsync(PushGateRequest request, CancellationToken cancellationToken) =>
            ValueTask.FromResult<PushSessionRecord?>(new PushSessionRecord(id.ToString("D"), ToolSessionRole.Actor, "untrusted"));
    }

    private sealed class CountingRequests : IPushApprovalRequests
    {
        public int Calls;

        public ValueTask<Guid?> FileAsync(PushGateRequest request, Guid sessionId, ToolSessionRole role, CancellationToken cancellationToken)
        {
            Calls++;
            return ValueTask.FromResult<Guid?>(null);
        }
    }

    private sealed class ThrowingRequests : IPushApprovalRequests
    {
        public ValueTask<Guid?> FileAsync(PushGateRequest request, Guid sessionId, ToolSessionRole role, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("store down");
    }
}
