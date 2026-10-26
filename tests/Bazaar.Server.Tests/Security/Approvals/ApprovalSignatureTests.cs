using System.Security.Cryptography;
using Bazaar.Server.Security.Approvals;
using Bazaar.Server.Security.Policy;

namespace Bazaar.Server.Tests.Security.Approvals;

public sealed class ApprovalSignatureTests : IDisposable
{
    private static readonly Guid _approvalId = Guid.Parse("0196a6f0-0000-7000-8000-000000000301");
    private static readonly byte[] _hash = Enumerable.Repeat((byte)1, DispatchHash.Length).ToArray();
    private static readonly DateTimeOffset _expiresAt = ApprovalTestData.Now.AddHours(24);

    private readonly ECDsa _key = Signing.NewKey();

    public void Dispose() => _key.Dispose();

    private byte[] SignedApprove() => Signing.Sign(_key, _approvalId, ApprovalDecision.Approve, _hash, _expiresAt);

    private bool VerifyAgainst(byte[] message, byte[] signature) =>
        ApprovalSignature.Verify(Signing.PublicKey(_key), message, signature);

    [Fact]
    public void Valid_signature_verifies()
    {
        var message = ApprovalSignature.Message(_approvalId, ApprovalDecision.Approve, _hash, _expiresAt, Signing.DeviceId);

        Assert.True(VerifyAgainst(message, SignedApprove()));
    }

    [Fact]
    public void Approve_signature_does_not_verify_as_deny()
    {
        var denyMessage = ApprovalSignature.Message(_approvalId, ApprovalDecision.Deny, _hash, _expiresAt, Signing.DeviceId);

        Assert.False(VerifyAgainst(denyMessage, SignedApprove()));
    }

    [Fact]
    public void Changed_args_hash_fails()
    {
        var otherHash = Enumerable.Repeat((byte)2, DispatchHash.Length).ToArray();
        var message = ApprovalSignature.Message(_approvalId, ApprovalDecision.Approve, otherHash, _expiresAt, Signing.DeviceId);

        Assert.False(VerifyAgainst(message, SignedApprove()));
    }

    [Fact]
    public void Changed_expiry_fails()
    {
        var message = ApprovalSignature.Message(_approvalId, ApprovalDecision.Approve, _hash, _expiresAt.AddSeconds(1), Signing.DeviceId);

        Assert.False(VerifyAgainst(message, SignedApprove()));
    }

    [Fact]
    public void Changed_approval_id_fails()
    {
        var message = ApprovalSignature.Message(Guid.NewGuid(), ApprovalDecision.Approve, _hash, _expiresAt, Signing.DeviceId);

        Assert.False(VerifyAgainst(message, SignedApprove()));
    }

    [Fact]
    public void Changed_device_id_fails()
    {
        var message = ApprovalSignature.Message(_approvalId, ApprovalDecision.Approve, _hash, _expiresAt, "device-2");

        Assert.False(VerifyAgainst(message, SignedApprove()));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(63)]
    [InlineData(65)]
    public void Wrong_length_signature_returns_false(int length)
    {
        var message = ApprovalSignature.Message(_approvalId, ApprovalDecision.Approve, _hash, _expiresAt, Signing.DeviceId);

        Assert.False(VerifyAgainst(message, new byte[length]));
    }

    [Fact]
    public void Non_spki_public_key_returns_false_without_throwing()
    {
        var message = ApprovalSignature.Message(_approvalId, ApprovalDecision.Approve, _hash, _expiresAt, Signing.DeviceId);
        var signature = SignedApprove();

        Assert.False(ApprovalSignature.Verify(new byte[] { 0x01, 0x02, 0x03 }, message, signature));
        Assert.False(ApprovalSignature.Verify([], message, signature));
    }

    [Theory]
    [InlineData("device\n1")]
    [InlineData("device 1")]
    [InlineData("device\r1")]
    public void Device_id_with_newline_or_space_is_rejected(string deviceId)
    {
        Assert.False(ApprovalSignature.IsValidDeviceId(deviceId));
        Assert.Throws<ArgumentException>(() =>
            ApprovalSignature.Message(_approvalId, ApprovalDecision.Approve, _hash, _expiresAt, deviceId));
    }

    [Fact]
    public void Device_id_with_trailing_newline_is_rejected()
    {
        Assert.False(ApprovalSignature.IsValidDeviceId("device-1\n"));
    }
}
