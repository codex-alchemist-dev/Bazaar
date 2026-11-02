using System.Security.Cryptography;
using Bazaar.Contracts.Approvals;
using Bazaar.Server.Security.Approvals;
using Bazaar.Server.Security.Policy;
using static Bazaar.Server.Tests.Security.Approvals.ApprovalTestData;
using DecisionOutcome = Bazaar.Server.Security.Approvals.ApprovalService.DecisionOutcome;

namespace Bazaar.Server.Tests.Security.Approvals;

public sealed class ApprovalDecisionMapperTests : IDisposable
{
    private readonly ECDsa _key = Signing.NewKey();
    private readonly InMemoryApprovalStore _store = new();
    private readonly ApprovalService _service;

    public ApprovalDecisionMapperTests()
    {
        _service = new ApprovalService(_store, Signing.RegistryWith(_key), new InMemoryRateBucketStore(), new ApprovalOptions());
    }

    public void Dispose() => _key.Dispose();

    private static string Base64(byte[] bytes) => Convert.ToBase64String(bytes);

    private static ApprovalDecisionDto Dto(
        Guid id,
        ApprovalDecisionKind kind = ApprovalDecisionKind.ApproveOnce,
        string? signature = null,
        string deviceId = Signing.DeviceId) =>
        new(id, kind, deviceId, signature, Now);

    private async Task<(ApprovalCardDto Card, ApprovalRequest Request)> CardAsync(string args = """{"number":12}""")
    {
        var result = await _service.RequestAsync(Context(args: args), Ct);
        var request = Present(result.Request);
        return (ApprovalCardMapper.ToCard(request, Now), request);
    }

    [Fact]
    public void Signed_approve_once_is_accepted_with_the_64_byte_signature()
    {
        var id = Guid.NewGuid();
        var signature = new byte[ApprovalSignature.SignatureLength];
        signature[0] = 7;

        var result = ApprovalDecisionMapper.Map(Dto(id, signature: Base64(signature)));

        Assert.True(result.IsAccepted);
        Assert.Equal(DecisionRejection.None, result.Rejection);
        var command = Present(result.Command);
        Assert.Equal(id, command.ApprovalId);
        Assert.Equal(ApprovalDecision.Approve, command.Decision);
        Assert.Equal(Signing.DeviceId, command.DeviceId);
        Assert.Equal(signature, command.Signature);
    }

    [Fact]
    public void Signed_deny_is_accepted_as_a_deny()
    {
        var result = ApprovalDecisionMapper.Map(Dto(Guid.NewGuid(), ApprovalDecisionKind.Deny, Base64(new byte[64])));

        Assert.True(result.IsAccepted);
        Assert.Equal(ApprovalDecision.Deny, Present(result.Command).Decision);
    }

    [Theory]
    [InlineData(ApprovalDecisionKind.EditThenApprove)]
    [InlineData(ApprovalDecisionKind.ApproveForTask)]
    public void Decisions_the_service_cannot_record_yet_are_rejected(ApprovalDecisionKind kind)
    {
        var result = ApprovalDecisionMapper.Map(Dto(Guid.NewGuid(), kind, Base64(new byte[64])));

        Assert.False(result.IsAccepted);
        Assert.Equal(DecisionRejection.UnsupportedDecision, result.Rejection);
        Assert.Null(result.Command);
    }

    [Fact]
    public void Undefined_decision_value_is_rejected_without_throwing()
    {
        var result = ApprovalDecisionMapper.Map(Dto(Guid.NewGuid(), (ApprovalDecisionKind)99, Base64(new byte[64])));

        Assert.Equal(DecisionRejection.UnknownDecision, result.Rejection);
        Assert.Null(result.Command);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void Decision_without_a_signature_is_never_accepted(string? signature)
    {
        var result = ApprovalDecisionMapper.Map(Dto(Guid.NewGuid(), signature: signature));

        Assert.False(result.IsAccepted);
        Assert.Equal(DecisionRejection.MissingSignature, result.Rejection);
        Assert.Null(result.Command);
    }

    [Theory]
    [InlineData("not base64!!")]
    [InlineData("%%%%")]
    public void Malformed_base64_is_rejected_without_throwing(string signature)
    {
        var result = ApprovalDecisionMapper.Map(Dto(Guid.NewGuid(), signature: signature));

        Assert.Equal(DecisionRejection.MalformedSignature, result.Rejection);
        Assert.Null(result.Command);
    }

    [Theory]
    [InlineData(63)]
    [InlineData(65)]
    public void Signature_of_the_wrong_length_is_rejected(int length)
    {
        var result = ApprovalDecisionMapper.Map(Dto(Guid.NewGuid(), signature: Base64(new byte[length])));

        Assert.Equal(DecisionRejection.MalformedSignature, result.Rejection);
        Assert.Null(result.Command);
    }

    [Fact]
    public void Device_id_with_a_disallowed_shape_is_rejected()
    {
        var result = ApprovalDecisionMapper.Map(Dto(Guid.NewGuid(), signature: Base64(new byte[64]), deviceId: "bad id!"));

        Assert.Equal(DecisionRejection.InvalidDeviceId, result.Rejection);
    }

    [Fact]
    public async Task Card_and_signature_agree_so_a_decision_on_the_card_is_recorded()
    {
        var (card, request) = await CardAsync();
        var signature = Signing.Sign(_key, card.ApprovalId, ApprovalDecision.Approve, request.ArgsHash, request.ExpiresAt);

        var mapped = ApprovalDecisionMapper.Map(Dto(card.ApprovalId, signature: Base64(signature)));
        var command = Present(mapped.Command);
        var outcome = await _service.DecideAsync(command.ApprovalId, command.Decision, command.DeviceId, command.Signature, Now.AddMinutes(1), Ct);

        Assert.Equal(DecisionOutcome.Recorded, outcome.Outcome);
    }

    [Fact]
    public async Task Signature_over_different_args_is_refused_by_the_service()
    {
        var (card, request) = await CardAsync();
        var otherHash = DispatchHash.Compute(Context(args: """{"number":13}"""));
        var signature = Signing.Sign(_key, card.ApprovalId, ApprovalDecision.Approve, otherHash, request.ExpiresAt);

        var mapped = ApprovalDecisionMapper.Map(Dto(card.ApprovalId, signature: Base64(signature)));
        var command = Present(mapped.Command);
        var outcome = await _service.DecideAsync(command.ApprovalId, command.Decision, command.DeviceId, command.Signature, Now.AddMinutes(1), Ct);

        Assert.Equal(DecisionOutcome.InvalidSignature, outcome.Outcome);
        Assert.Equal(ApprovalStatus.Pending, Present(outcome.Request).Status);
    }
}
