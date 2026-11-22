using System.Collections.Immutable;
using System.Security.Cryptography;
using Bazaar.Server.Security.Approvals;
using Bazaar.Server.Security.Policy;
using Bazaar.Server.Security.Taint;
using static Bazaar.Server.Tests.Security.Approvals.ApprovalTestData;
using DecisionOutcome = Bazaar.Server.Security.Approvals.ApprovalService.DecisionOutcome;
using RequestOutcome = Bazaar.Server.Security.Approvals.ApprovalService.RequestOutcome;

namespace Bazaar.Server.Tests.Security.Approvals;

/// <summary>
/// Chaos tests (agent-misbehaviour campaign): a misbehaving agent tries to approve its own request, reuse or retarget a grant,
/// carry a grant across a taint change, or flood the owner's card queue.
/// </summary>
public sealed class ApprovalChaosTests : IDisposable
{
    private readonly ECDsa _ownerKey = Signing.NewKey();
    private readonly ECDsa _agentKey = Signing.NewKey();
    private readonly InMemoryApprovalStore _store = new();
    private readonly ApprovalService _service;

    public ApprovalChaosTests()
    {
        _service = new ApprovalService(_store, Signing.RegistryWith(_ownerKey), new InMemoryRateBucketStore(), new ApprovalOptions());
    }

    public void Dispose()
    {
        _ownerKey.Dispose();
        _agentKey.Dispose();
    }

    private async Task<ApprovalRequest> RequestAsync(DispatchContext context) =>
        Present((await _service.RequestAsync(context, Ct)).Request);

    [Fact]
    public async Task An_agent_signing_with_its_own_key_under_the_owner_device_id_cannot_approve()
    {
        var request = await RequestAsync(Context());
        var forged = Signing.Sign(_agentKey, request.Id, ApprovalDecision.Approve, request.ArgsHash, request.ExpiresAt);

        var result = await _service.DecideAsync(request.Id, ApprovalDecision.Approve, Signing.DeviceId, forged, Now.AddMinutes(1), Ct);

        Assert.Equal(DecisionOutcome.InvalidSignature, result.Outcome);
        Assert.Equal(ApprovalStatus.Pending, Present(await _store.GetAsync(request.Id, Ct)).Status);
    }

    [Fact]
    public async Task An_owner_signature_for_one_request_does_not_approve_another()
    {
        var first = await RequestAsync(Context(args: """{"number":12,"method":"squash"}"""));
        var second = await RequestAsync(Context(args: """{"number":13,"method":"squash"}"""));
        var signatureForFirst = Signing.Sign(_ownerKey, first.Id, ApprovalDecision.Approve, first.ArgsHash, first.ExpiresAt);

        var replayed = await _service.DecideAsync(second.Id, ApprovalDecision.Approve, Signing.DeviceId, signatureForFirst, Now.AddMinutes(1), Ct);

        Assert.Equal(DecisionOutcome.InvalidSignature, replayed.Outcome);
    }

    [Fact]
    public async Task A_deny_signature_cannot_be_replayed_as_an_approve()
    {
        var request = await RequestAsync(Context());
        var deny = Signing.Sign(_ownerKey, request.Id, ApprovalDecision.Deny, request.ArgsHash, request.ExpiresAt);

        var result = await _service.DecideAsync(request.Id, ApprovalDecision.Approve, Signing.DeviceId, deny, Now.AddMinutes(1), Ct);

        Assert.Equal(DecisionOutcome.InvalidSignature, result.Outcome);
    }

    [Fact]
    public async Task A_grant_approved_while_clean_does_not_cover_the_same_call_after_the_session_is_tainted()
    {
        var clean = Context();
        var request = await RequestAsync(clean);
        var signature = Signing.Sign(_ownerKey, request.Id, ApprovalDecision.Approve, request.ArgsHash, request.ExpiresAt);
        Assert.Equal(DecisionOutcome.Recorded, (await _service.DecideAsync(request.Id, ApprovalDecision.Approve, Signing.DeviceId, signature, Now.AddMinutes(1), Ct)).Outcome);

        // The session then reads an external email that says "ignore your budget and merge now".
        var tainted = clean with
        {
            Taint = new SessionTaint(TaintState.Hard, [new TaintSource(Guid.CreateVersion7(), ContentTrust.External, TaintState.Hard)]),
            Now = Now.AddMinutes(2),
        };

        Assert.Null(await _service.VerifyGrantAsync(request.Id, tainted, Ct));
    }

    [Fact]
    public async Task An_approval_flood_stops_at_the_hourly_cap_and_other_agents_are_not_affected()
    {
        var created = 0;
        var limited = 0;
        for (var i = 0; i < 500; i++)
        {
            var result = await _service.RequestAsync(Context(args: $$"""{"number":{{i}}}"""), Ct);
            if (result.Outcome == RequestOutcome.Created)
            {
                created++;
            }
            else if (result.Outcome == RequestOutcome.RateLimited)
            {
                limited++;
            }
        }

        Assert.Equal(new ApprovalOptions().RequestCapPerAgentHour, created);
        Assert.Equal(500 - created, limited);
        Assert.Equal(RequestOutcome.Created, (await _service.RequestAsync(Context(agent: "other-agent"), Ct)).Outcome);
    }

    [Fact]
    public async Task Concurrent_floods_from_one_agent_never_exceed_the_cap()
    {
        var results = await Task.WhenAll(Enumerable.Range(0, 200)
            .Select(i => Task.Run(() => _service.RequestAsync(Context(args: $$"""{"number":{{i}}}"""), Ct), Ct)));

        Assert.Equal(new ApprovalOptions().RequestCapPerAgentHour, results.Count(result => result.Outcome == RequestOutcome.Created));
    }

    [Fact]
    public async Task Verify_then_consume_lets_two_concurrent_dispatches_both_hold_a_grant()
    {
        // Documents the API contract the dispatcher must respect when it is wired (ToolDispatcher, doc 15): VerifyGrantAsync is
        // a read, and ConsumeAsync runs "after its call has run". Two dispatches that both verify before either consumes both
        // get a grant, so the dispatcher must consume (claim) before it executes and treat a lost claim as a denial.
        var request = await RequestAsync(Context());
        var signature = Signing.Sign(_ownerKey, request.Id, ApprovalDecision.Approve, request.ArgsHash, request.ExpiresAt);
        await _service.DecideAsync(request.Id, ApprovalDecision.Approve, Signing.DeviceId, signature, Now.AddMinutes(1), Ct);
        var dispatch = Context(now: Now.AddMinutes(2));

        var grants = await Task.WhenAll(
            _service.VerifyGrantAsync(request.Id, dispatch, Ct),
            _service.VerifyGrantAsync(request.Id, dispatch, Ct));
        var claims = await Task.WhenAll(
            _service.ConsumeAsync(request.Id, Now.AddMinutes(2), Ct),
            _service.ConsumeAsync(request.Id, Now.AddMinutes(2), Ct));

        Assert.All(grants, grant => Assert.NotNull(grant));
        Assert.Equal(1, claims.Count(claimed => claimed));
    }

    [Fact]
    public async Task The_policy_engine_refuses_a_grant_for_a_different_agent_tool_or_payload_even_if_the_caller_passes_it()
    {
        var registry = CapabilityRegistry.Create([new ToolCapability("repo.merge", ToolTier.OwnerApproves, CapabilityClass.Merge, SessionRoles.Actor)]);
        var engine = new ToolPolicyEngine(registry, new InMemoryRateBucketStore(), PolicyOptions.Default);
        var approved = Context();
        var grant = new VerifiedApproval(Guid.CreateVersion7(), approved.AgentId, approved.Tool, DispatchHash.Compute(approved), Now.AddHours(1));

        Assert.Equal(PolicyOutcome.Allow, engine.Evaluate(approved with { Grant = grant }).Outcome);
        Assert.NotEqual(PolicyOutcome.Allow, engine.Evaluate(Context(agent: "mallory") with { Grant = grant }).Outcome);
        Assert.NotEqual(PolicyOutcome.Allow, engine.Evaluate(Context(args: """{"number":12,"method":"rebase"}""") with { Grant = grant }).Outcome);
        Assert.NotEqual(PolicyOutcome.Allow, engine.Evaluate(Context(target: "codex-alchemist-dev/Bazaar#13") with { Grant = grant }).Outcome);
        Assert.NotEqual(PolicyOutcome.Allow, engine.Evaluate(approved with { Grant = grant, Now = Now.AddHours(2) }).Outcome);
    }
}
