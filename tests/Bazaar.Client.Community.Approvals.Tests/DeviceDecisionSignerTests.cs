using System.Security.Cryptography;
using Bazaar.Approvals;
using Bazaar.Client.Approvals;
using Bazaar.Contracts.Approvals;
using Bazaar.Server.Security.Approvals;

namespace Bazaar.Client.Community.Approvals.Tests;

public sealed class DeviceDecisionSignerTests
{
    private const string _deviceId = "phone-1";
    private static readonly DateTimeOffset _now = new(2026, 11, 20, 12, 0, 0, TimeSpan.Zero);
    private static readonly string _hash = new('a', 64);

    private sealed class EcKeySigner : IDeviceApprovalSigner, IDisposable
    {
        private readonly ECDsa _key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        public int Calls;
        public string DeviceId => DeviceDecisionSignerTests._deviceId;
        public byte[] PublicKeySpki => _key.ExportSubjectPublicKeyInfo();

        public ValueTask<byte[]> SignAsync(ReadOnlyMemory<byte> canonicalPayload, CancellationToken cancellationToken)
        {
            Calls++;
            return ValueTask.FromResult(_key.SignData(canonicalPayload.Span, HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation));
        }

        public void Dispose() => _key.Dispose();
    }

    private sealed class FakeBiometric(BiometricOutcome outcome) : IBiometricPrompt
    {
        public int Calls;

        public ValueTask<BiometricOutcome> AuthenticateAsync(BiometricRequest request, CancellationToken cancellationToken)
        {
            Calls++;
            return ValueTask.FromResult(outcome);
        }
    }

    private static ApprovalCardDto Card(ApprovalCardStatus status = ApprovalCardStatus.Pending, DateTimeOffset? expires = null) => new(
        Guid.Parse("11111111-2222-3333-4444-555555555555"), "agent-1", "community.post", "o/r", "Post", "body", _hash, [], null,
        expires ?? _now.AddMinutes(10), status,
        [ApprovalDecisionKind.ApproveOnce, ApprovalDecisionKind.Deny, ApprovalDecisionKind.EditThenApprove]);

    [Theory]
    [InlineData(ApprovalDecisionKind.ApproveOnce, ApprovalDecision.Approve)]
    [InlineData(ApprovalDecisionKind.Deny, ApprovalDecision.Deny)]
    [InlineData(ApprovalDecisionKind.EditThenApprove, ApprovalDecision.Deny)]
    public async Task Signature_verifies_against_the_server_message(ApprovalDecisionKind kind, ApprovalDecision serverDecision)
    {
        using var key = new EcKeySigner();
        var sut = new DeviceDecisionSigner(new FakeBiometric(BiometricOutcome.Succeeded), key);
        var card = Card();

        var dto = await sut.SignAsync(card, kind, _now, CancellationToken.None);

        Assert.Equal(kind, dto.Decision);
        Assert.Equal(_deviceId, dto.DeviceId);
        Assert.NotNull(dto.SignatureBase64);
        var message = ApprovalSignature.Message(card.ApprovalId, serverDecision, Convert.FromHexString(_hash), card.ExpiresAt, _deviceId);
        Assert.True(ApprovalSignature.Verify(key.PublicKeySpki, message, Convert.FromBase64String(dto.SignatureBase64)));
        var other = serverDecision == ApprovalDecision.Approve ? ApprovalDecision.Deny : ApprovalDecision.Approve;
        var wrong = ApprovalSignature.Message(card.ApprovalId, other, Convert.FromHexString(_hash), card.ExpiresAt, _deviceId);
        Assert.False(ApprovalSignature.Verify(key.PublicKeySpki, wrong, Convert.FromBase64String(dto.SignatureBase64)));
    }

    [Theory]
    [InlineData(BiometricOutcome.Cancelled)]
    [InlineData(BiometricOutcome.Failed)]
    [InlineData(BiometricOutcome.Unavailable)]
    public async Task Approve_without_a_biometric_success_is_unsigned(BiometricOutcome outcome)
    {
        using var key = new EcKeySigner();
        var sut = new DeviceDecisionSigner(new FakeBiometric(outcome), key);

        var dto = await sut.SignAsync(Card(), ApprovalDecisionKind.ApproveOnce, _now, CancellationToken.None);

        Assert.Null(dto.SignatureBase64);
        Assert.Equal(0, key.Calls);
    }

    [Fact]
    public async Task Deny_needs_no_biometric()
    {
        using var key = new EcKeySigner();
        var bio = new FakeBiometric(BiometricOutcome.Failed);
        var sut = new DeviceDecisionSigner(bio, key);

        var dto = await sut.SignAsync(Card(), ApprovalDecisionKind.Deny, _now, CancellationToken.None);

        Assert.NotNull(dto.SignatureBase64);
        Assert.Equal(0, bio.Calls);
    }

    [Fact]
    public async Task Expired_or_non_pending_card_or_unsupported_kind_is_unsigned()
    {
        using var key = new EcKeySigner();
        var sut = new DeviceDecisionSigner(new FakeBiometric(BiometricOutcome.Succeeded), key);

        Assert.Null((await sut.SignAsync(Card(expires: _now.AddSeconds(-1)), ApprovalDecisionKind.ApproveOnce, _now, CancellationToken.None)).SignatureBase64);
        Assert.Null((await sut.SignAsync(Card(ApprovalCardStatus.Expired), ApprovalDecisionKind.ApproveOnce, _now, CancellationToken.None)).SignatureBase64);
        Assert.Null((await sut.SignAsync(Card(), ApprovalDecisionKind.ApproveForTask, _now, CancellationToken.None)).SignatureBase64);
        Assert.Equal(0, key.Calls);
    }

    [Fact]
    public async Task Failing_signer_or_wrong_length_signature_is_unsigned()
    {
        var sut = new DeviceDecisionSigner(new FakeBiometric(BiometricOutcome.Succeeded), new FixedSigner(new byte[10]));
        Assert.Null((await sut.SignAsync(Card(), ApprovalDecisionKind.Deny, _now, CancellationToken.None)).SignatureBase64);

        var throwing = new DeviceDecisionSigner(new FakeBiometric(BiometricOutcome.Succeeded), new FixedSigner(null));
        Assert.Null((await throwing.SignAsync(Card(), ApprovalDecisionKind.Deny, _now, CancellationToken.None)).SignatureBase64);
    }

    [Fact]
    public void Bad_device_id_is_refused_at_construction()
    {
        Assert.Throws<ArgumentException>(() => new DeviceDecisionSigner(new FakeBiometric(BiometricOutcome.Succeeded), new FixedSigner(new byte[64], "bad id\n")));
    }

    private sealed class FixedSigner(byte[]? signature, string deviceId = _deviceId) : IDeviceApprovalSigner
    {
        public string DeviceId => deviceId;

        public ValueTask<byte[]> SignAsync(ReadOnlyMemory<byte> canonicalPayload, CancellationToken cancellationToken) =>
            signature is null ? throw new InvalidOperationException("no key") : ValueTask.FromResult(signature);
    }
}
