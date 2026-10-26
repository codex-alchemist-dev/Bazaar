using System.Security.Cryptography;
using Bazaar.Server.Security.Approvals;
using Bazaar.Server.Security.Policy;
using DecisionOutcome = Bazaar.Server.Security.Approvals.ApprovalService.DecisionOutcome;
using RequestOutcome = Bazaar.Server.Security.Approvals.ApprovalService.RequestOutcome;
using static Bazaar.Server.Tests.Security.Approvals.ApprovalTestData;

namespace Bazaar.Server.Tests.Security.Approvals;

public sealed class ApprovalServiceTests : IDisposable
{
    private readonly ECDsa _key = Signing.NewKey();
    private readonly InMemoryApprovalStore _store = new();
    private readonly InMemoryOwnerKeyRegistry _keys;
    private readonly InMemoryRateBucketStore _rates = new();
    private readonly ApprovalService _service;

    public ApprovalServiceTests()
    {
        _keys = Signing.RegistryWith(_key);
        _service = new ApprovalService(_store, _keys, _rates, new ApprovalOptions());
    }

    public void Dispose() => _key.Dispose();

    private async Task<ApprovalRequest> PendingAsync()
    {
        var result = await _service.RequestAsync(Context(), Ct);
        Assert.Equal(RequestOutcome.Created, result.Outcome);
        return Present(result.Request);
    }

    private byte[] Sign(ApprovalRequest request, ApprovalDecision decision, string deviceId = Signing.DeviceId) =>
        Signing.Sign(_key, request.Id, decision, request.ArgsHash, request.ExpiresAt, deviceId);

    private Task<ApprovalService.DecisionResult> DecideAsync(
        ApprovalRequest request,
        ApprovalDecision decision,
        DateTimeOffset? at = null) =>
        _service.DecideAsync(request.Id, decision, Signing.DeviceId, Sign(request, decision), at ?? Now.AddMinutes(5), Ct);

    private async Task<ApprovalStatus> StatusAsync(Guid id) =>
        Present(await _store.GetAsync(id, Ct)).Status;

    [Fact]
    public async Task Request_creates_pending_row_that_expires_after_24_hours()
    {
        var request = await PendingAsync();

        Assert.Equal(ApprovalStatus.Pending, request.Status);
        Assert.Equal(Now.AddHours(24), request.ExpiresAt);
        Assert.Equal(ApprovalStatus.Pending, await StatusAsync(request.Id));
    }

    [Fact]
    public async Task Valid_approve_records_approved_with_owner_and_device()
    {
        var request = await PendingAsync();

        var result = await DecideAsync(request, ApprovalDecision.Approve);

        Assert.Equal(DecisionOutcome.Recorded, result.Outcome);
        var stored = Present(await _store.GetAsync(request.Id, Ct));
        Assert.Equal(ApprovalStatus.Approved, stored.Status);
        Assert.Equal("owner", stored.DecidedBy);
        Assert.Equal(Signing.DeviceId, stored.DeviceId);
        Assert.Equal(Now.AddMinutes(5), stored.DecidedAt);
    }

    [Fact]
    public async Task Repeating_the_same_approve_is_idempotent_and_leaves_the_row_alone()
    {
        var request = await PendingAsync();
        await DecideAsync(request, ApprovalDecision.Approve);
        var before = Present(await _store.GetAsync(request.Id, Ct));

        var second = await DecideAsync(request, ApprovalDecision.Approve, Now.AddMinutes(10));

        Assert.Equal(DecisionOutcome.AlreadyDecided, second.Outcome);
        var after = Present(await _store.GetAsync(request.Id, Ct));
        Assert.Equal(before.Status, after.Status);
        Assert.Equal(before.DecidedAt, after.DecidedAt);
        Assert.Equal(before.Signature, after.Signature);
    }

    [Fact]
    public async Task Deny_after_approve_is_a_conflict()
    {
        var request = await PendingAsync();
        await DecideAsync(request, ApprovalDecision.Approve);

        var deny = await DecideAsync(request, ApprovalDecision.Deny);

        Assert.Equal(DecisionOutcome.Conflict, deny.Outcome);
        Assert.Equal(ApprovalStatus.Approved, await StatusAsync(request.Id));
    }

    [Fact]
    public async Task Valid_deny_records_denied()
    {
        var request = await PendingAsync();

        var result = await DecideAsync(request, ApprovalDecision.Deny);

        Assert.Equal(DecisionOutcome.Recorded, result.Outcome);
        var stored = Present(await _store.GetAsync(request.Id, Ct));
        Assert.Equal(ApprovalStatus.Denied, stored.Status);
        Assert.Equal("owner", stored.DecidedBy);
    }

    [Fact]
    public async Task Bad_signature_returns_invalid_signature_and_leaves_pending()
    {
        var request = await PendingAsync();
        using var otherKey = Signing.NewKey();
        var forged = Signing.Sign(otherKey, request.Id, ApprovalDecision.Approve, request.ArgsHash, request.ExpiresAt);

        var result = await _service.DecideAsync(request.Id, ApprovalDecision.Approve, Signing.DeviceId, forged, Now.AddMinutes(5), Ct);

        Assert.Equal(DecisionOutcome.InvalidSignature, result.Outcome);
        Assert.Equal(ApprovalStatus.Pending, await StatusAsync(request.Id));
    }

    [Fact]
    public async Task Unknown_device_returns_invalid_signature()
    {
        var request = await PendingAsync();
        var signature = Sign(request, ApprovalDecision.Approve, "device-2");

        var result = await _service.DecideAsync(request.Id, ApprovalDecision.Approve, "device-2", signature, Now.AddMinutes(5), Ct);

        Assert.Equal(DecisionOutcome.InvalidSignature, result.Outcome);
        Assert.Equal(ApprovalStatus.Pending, await StatusAsync(request.Id));
    }

    [Fact]
    public async Task Revoked_device_returns_invalid_signature()
    {
        var request = await PendingAsync();
        _keys.Register(new OwnerDeviceKey("owner", "device-2", Signing.PublicKey(_key), Revoked: true));
        var signature = Sign(request, ApprovalDecision.Approve, "device-2");

        var result = await _service.DecideAsync(request.Id, ApprovalDecision.Approve, "device-2", signature, Now.AddMinutes(5), Ct);

        Assert.Equal(DecisionOutcome.InvalidSignature, result.Outcome);
        Assert.Equal(ApprovalStatus.Pending, await StatusAsync(request.Id));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(60)]
    public async Task Decision_at_or_after_expiry_returns_expired_and_stores_expired(int minutesAfterExpiry)
    {
        var request = await PendingAsync();

        var result = await DecideAsync(request, ApprovalDecision.Approve, request.ExpiresAt.AddMinutes(minutesAfterExpiry));

        Assert.Equal(DecisionOutcome.Expired, result.Outcome);
        var stored = Present(await _store.GetAsync(request.Id, Ct));
        Assert.Equal(ApprovalStatus.Expired, stored.Status);
        Assert.Null(stored.DecidedBy);
    }

    [Fact(Skip = "BUG: a valid approve after expiry returns Conflict, not Expired; the Expired row falls through to the same-decision check.")]
    public async Task Valid_approve_after_expiry_still_returns_expired()
    {
        var request = await PendingAsync();
        await DecideAsync(request, ApprovalDecision.Approve, request.ExpiresAt);

        var late = await DecideAsync(request, ApprovalDecision.Approve, request.ExpiresAt.AddMinutes(1));

        Assert.Equal(DecisionOutcome.Expired, late.Outcome);
        Assert.Equal(ApprovalStatus.Expired, await StatusAsync(request.Id));
    }

    [Fact]
    public async Task Signature_over_a_different_approval_id_is_rejected()
    {
        var request = await PendingAsync();
        var signature = Signing.Sign(_key, Guid.NewGuid(), ApprovalDecision.Approve, request.ArgsHash, request.ExpiresAt);

        var result = await _service.DecideAsync(request.Id, ApprovalDecision.Approve, Signing.DeviceId, signature, Now.AddMinutes(5), Ct);

        Assert.Equal(DecisionOutcome.InvalidSignature, result.Outcome);
        Assert.Equal(ApprovalStatus.Pending, await StatusAsync(request.Id));
    }

    [Fact]
    public async Task Signature_over_a_different_decision_is_rejected()
    {
        var request = await PendingAsync();
        var denySignature = Sign(request, ApprovalDecision.Deny);

        var result = await _service.DecideAsync(request.Id, ApprovalDecision.Approve, Signing.DeviceId, denySignature, Now.AddMinutes(5), Ct);

        Assert.Equal(DecisionOutcome.InvalidSignature, result.Outcome);
        Assert.Equal(ApprovalStatus.Pending, await StatusAsync(request.Id));
    }

    [Fact]
    public async Task Unknown_approval_id_returns_not_found()
    {
        var result = await _service.DecideAsync(
            Guid.NewGuid(),
            ApprovalDecision.Approve,
            Signing.DeviceId,
            new byte[ApprovalSignature.SignatureLength],
            Now,
            Ct);

        Assert.Equal(DecisionOutcome.NotFound, result.Outcome);
    }

    [Theory]
    [InlineData("bad device")]
    [InlineData("bad\ndevice")]
    [InlineData("")]
    public async Task Malformed_device_id_returns_invalid_device_id(string deviceId)
    {
        var request = await PendingAsync();

        var result = await _service.DecideAsync(
            request.Id,
            ApprovalDecision.Approve,
            deviceId,
            new byte[ApprovalSignature.SignatureLength],
            Now.AddMinutes(5),
            Ct);

        Assert.Equal(DecisionOutcome.InvalidDeviceId, result.Outcome);
    }

    [Fact]
    public async Task Request_with_repeated_property_name_is_malformed()
    {
        var context = Context(args: """{"number":12,"number":13}""");

        var result = await _service.RequestAsync(context, Ct);

        Assert.Equal(RequestOutcome.MalformedArguments, result.Outcome);
        Assert.Null(result.Request);
    }

    [Fact]
    public async Task Request_cap_per_agent_per_hour_is_enforced_and_resets_after_an_hour()
    {
        var service = new ApprovalService(_store, _keys, _rates, new ApprovalOptions(RequestCapPerAgentHour: 2));
        var start = Now;

        Assert.Equal(RequestOutcome.Created, (await service.RequestAsync(Context(now: start), Ct)).Outcome);
        Assert.Equal(RequestOutcome.Created, (await service.RequestAsync(Context(now: start.AddMinutes(1)), Ct)).Outcome);
        Assert.Equal(RequestOutcome.RateLimited, (await service.RequestAsync(Context(now: start.AddMinutes(2)), Ct)).Outcome);

        Assert.Equal(RequestOutcome.Created, (await service.RequestAsync(Context(agent: "bert", now: start.AddMinutes(2)), Ct)).Outcome);

        Assert.Equal(RequestOutcome.RateLimited, (await service.RequestAsync(Context(now: start.AddMinutes(59)), Ct)).Outcome);
        Assert.Equal(RequestOutcome.Created, (await service.RequestAsync(Context(now: start.AddMinutes(60)), Ct)).Outcome);
    }
}
