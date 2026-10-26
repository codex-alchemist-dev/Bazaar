using System.Security.Cryptography;
using Bazaar.Server.Security.Approvals;
using Bazaar.Server.Security.Policy;
using DecisionOutcome = Bazaar.Server.Security.Approvals.ApprovalService.DecisionOutcome;
using static Bazaar.Server.Tests.Security.Approvals.ApprovalTestData;

namespace Bazaar.Server.Tests.Security.Approvals;

public sealed class ApprovalGrantTests : IDisposable
{
    private readonly ECDsa _key = Signing.NewKey();
    private readonly InMemoryApprovalStore _store = new();
    private readonly ApprovalService _service;

    public ApprovalGrantTests()
    {
        _service = new ApprovalService(_store, Signing.RegistryWith(_key), new InMemoryRateBucketStore(), new ApprovalOptions());
    }

    public void Dispose() => _key.Dispose();

    private async Task<ApprovalRequest> RequestAsync(DispatchContext context) =>
        Present((await _service.RequestAsync(context, Ct)).Request);

    private async Task<ApprovalRequest> ApproveAsync(DispatchContext context)
    {
        var request = await RequestAsync(context);
        var signature = Signing.Sign(_key, request.Id, ApprovalDecision.Approve, request.ArgsHash, request.ExpiresAt);

        var result = await _service.DecideAsync(request.Id, ApprovalDecision.Approve, Signing.DeviceId, signature, Now.AddMinutes(5), Ct);

        Assert.Equal(DecisionOutcome.Recorded, result.Outcome);
        return Present(result.Request);
    }

    /// <summary>Builds a request in the given status, for the status matrix below.</summary>
    private async Task<ApprovalRequest> InStatusAsync(ApprovalStatus status)
    {
        switch (status)
        {
            case ApprovalStatus.Pending:
                return await RequestAsync(Context());

            case ApprovalStatus.Denied:
            {
                var request = await RequestAsync(Context());
                var signature = Signing.Sign(_key, request.Id, ApprovalDecision.Deny, request.ArgsHash, request.ExpiresAt);
                await _service.DecideAsync(request.Id, ApprovalDecision.Deny, Signing.DeviceId, signature, Now.AddMinutes(5), Ct);
                return Present(await _store.GetAsync(request.Id, Ct));
            }

            case ApprovalStatus.Expired:
            {
                var request = await RequestAsync(Context());
                var signature = Signing.Sign(_key, request.Id, ApprovalDecision.Approve, request.ArgsHash, request.ExpiresAt);
                await _service.DecideAsync(request.Id, ApprovalDecision.Approve, Signing.DeviceId, signature, request.ExpiresAt, Ct);
                return Present(await _store.GetAsync(request.Id, Ct));
            }

            case ApprovalStatus.Consumed:
            {
                var request = await ApproveAsync(Context());
                await _service.ConsumeAsync(request.Id, Now.AddMinutes(10), Ct);
                return Present(await _store.GetAsync(request.Id, Ct));
            }

            default:
                throw new ArgumentOutOfRangeException(nameof(status), status, null);
        }
    }

    [Fact]
    public async Task Grant_is_returned_for_an_approved_unexpired_matching_call()
    {
        var request = await ApproveAsync(Context());

        var grant = await _service.VerifyGrantAsync(request.Id, Context(now: Now.AddHours(1)), Ct);

        var verified = Present(grant);
        Assert.Equal(request.Id, verified.ApprovalId);
        Assert.Equal("charles", verified.AgentId);
        Assert.Equal("repo.merge", verified.Tool);
        Assert.Equal(request.ArgsHash, verified.ArgsHash);
        Assert.Equal(request.ExpiresAt, verified.ExpiresAt);
    }

    [Theory]
    [InlineData(ApprovalStatus.Pending)]
    [InlineData(ApprovalStatus.Denied)]
    [InlineData(ApprovalStatus.Expired)]
    [InlineData(ApprovalStatus.Consumed)]
    public async Task Grant_is_null_unless_the_request_is_approved(ApprovalStatus status)
    {
        var request = await InStatusAsync(status);

        var grant = await _service.VerifyGrantAsync(request.Id, Context(now: Now.AddMinutes(10)), Ct);

        Assert.Null(grant);
    }

    [Theory]
    [InlineData("""{"number":13,"method":"squash"}""")]
    [InlineData("""{"number":12,"method":"Squash"}""")]
    [InlineData("""{"number":12,"method":"squash "}""")]
    [InlineData("""{"number":12,"method":"squash","extra":true}""")]
    public async Task Grant_is_null_when_args_change_by_one_character(string changedArgs)
    {
        var request = await ApproveAsync(Context());

        var grant = await _service.VerifyGrantAsync(request.Id, Context(args: changedArgs), Ct);

        Assert.Null(grant);
    }

    [Fact]
    public async Task Grant_is_null_when_the_target_changes()
    {
        var request = await ApproveAsync(Context());

        var grant = await _service.VerifyGrantAsync(request.Id, Context(target: "codex-alchemist-dev/Bazaar#13"), Ct);

        Assert.Null(grant);
    }

    [Theory]
    [InlineData("bert", "repo.merge")]
    [InlineData("charles", "repo.push")]
    public async Task Grant_is_null_when_the_agent_or_tool_differ(string agent, string tool)
    {
        var request = await ApproveAsync(Context());

        var grant = await _service.VerifyGrantAsync(request.Id, Context(agent: agent, tool: tool), Ct);

        Assert.Null(grant);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    public async Task Grant_is_null_at_or_after_expiry(int minutesAfterExpiry)
    {
        var request = await ApproveAsync(Context());

        var grant = await _service.VerifyGrantAsync(
            request.Id,
            Context(now: request.ExpiresAt.AddMinutes(minutesAfterExpiry)),
            Ct);

        Assert.Null(grant);
    }

    [Fact]
    public async Task Consume_returns_true_once_then_false()
    {
        var request = await ApproveAsync(Context());
        var now = Now.AddMinutes(10);

        Assert.True(await _service.ConsumeAsync(request.Id, now, Ct));
        Assert.False(await _service.ConsumeAsync(request.Id, now, Ct));
        Assert.Equal(ApprovalStatus.Consumed, Present(await _store.GetAsync(request.Id, Ct)).Status);
    }

    [Fact]
    public async Task Grant_cannot_be_consumed_twice_concurrently()
    {
        var request = await ApproveAsync(Context());
        var now = Now.AddMinutes(10);

        var results = await Task.WhenAll(
            Enumerable.Range(0, 20).Select(_ => Task.Run(() => _service.ConsumeAsync(request.Id, now, Ct), Ct)));

        Assert.Equal(1, results.Count(consumed => consumed));
    }
}
