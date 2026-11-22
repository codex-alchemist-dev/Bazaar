using System.Security.Cryptography;
using Bazaar.Approvals;
using Bazaar.Client.Approvals;
using Bazaar.Client.GitGui.Merge;
using Bazaar.DeviceApproval;

namespace Bazaar.Client.Approvals.Desktop.Tests;

/// <summary>A P-256 software key standing in for the TPM key, with a settable custody and failure.</summary>
internal sealed class FakePlatformSigner(ApprovalKeyCustody custody = ApprovalKeyCustody.Tpm) : IApprovalSigner, IDisposable
{
    private readonly ECDsa _key = ECDsa.Create(ECCurve.NamedCurves.nistP256);

    public ApprovalKeyCustody Custody => custody;

    public ApprovalRefusal? Refuse { get; set; }

    public byte[]? ForcedSignature { get; set; }

    public int Signed { get; private set; }

    public byte[] PublicKeySpki => _key.ExportSubjectPublicKeyInfo();

    public ValueTask<ApprovalSignature> SignAsync(ReadOnlyMemory<byte> payload, CancellationToken cancellationToken)
    {
        if (Refuse is { } reason)
        {
            throw new ApprovalRefusedException(reason, "refused");
        }

        Signed++;
        var sig = ForcedSignature ?? _key.SignData(payload.Span, HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
        return ValueTask.FromResult(new ApprovalSignature(sig, "k", ApprovalKeyCustody.Tpm));
    }

    public void Dispose() => _key.Dispose();
}

internal sealed class FakePresence(PresenceOutcome outcome = PresenceOutcome.Verified) : IPresenceVerifier
{
    public List<string> Reasons { get; } = [];

    public ValueTask<PresenceOutcome> VerifyAsync(string reason, CancellationToken cancellationToken)
    {
        Reasons.Add(reason);
        return ValueTask.FromResult(outcome);
    }
}

internal sealed class Identity : IOwnerMergeIdentity
{
    public Guid HumanId { get; } = Guid.Parse("11111111-2222-3333-4444-555555555555");

    public string SignOff => "Signed-off-by: Owner <owner@example.test>";
}

public sealed class DesktopApprovalTests
{
    private const string _head = "0123456789abcdef0123456789abcdef01234567";
    private static readonly MergeApprovalRequest _request = new("github", "acme/widgets", 7, _head, "Title (#7)");

    [Fact]
    public async Task Merge_approval_round_trips_and_the_signature_verifies_against_the_public_key()
    {
        using var platform = new FakePlatformSigner();
        var approver = new DeviceOwnerMergeApprover(
            new DesktopBiometricPrompt(new FakePresence()), new DesktopDeviceApprovalSigner("owner-pc.1", platform), new Identity());

        var result = await approver.ApproveAsync(_request, TestContext.Current.CancellationToken);

        Assert.Equal(MergeApprovalOutcome.Approved, result.Outcome);
        var a = result.Merge!.Approval;
        Assert.Equal("owner-pc.1", a.DeviceId);
        var payload = new OwnerPayload(
            a.Action, a.ApprovalId, Convert.FromBase64String(a.ArgsHashBase64), a.HeadSha, a.HumanId, a.DeviceId,
            a.IssuedAt, a.ExpiresAt, Convert.FromBase64String(a.NonceBase64));
        var signature = Convert.FromBase64String(a.SignatureBase64);
        Assert.Equal(64, signature.Length);
        Assert.True(OwnerSignatureCrypto.Verify(platform.PublicKeySpki, OwnerSignedPayload.Build(payload), signature));
    }

    [Theory]
    [InlineData(PresenceOutcome.Cancelled, BiometricOutcome.Cancelled)]
    [InlineData(PresenceOutcome.Unavailable, BiometricOutcome.Unavailable)]
    [InlineData(PresenceOutcome.Failed, BiometricOutcome.Failed)]
    [InlineData(PresenceOutcome.Verified, BiometricOutcome.Succeeded)]
    public async Task Presence_outcomes_map_one_to_one(PresenceOutcome presence, BiometricOutcome expected)
    {
        var prompt = new DesktopBiometricPrompt(new FakePresence(presence));
        var outcome = await prompt.AuthenticateAsync(new BiometricRequest(Guid.NewGuid(), "abcdef0123456789", "Approve merge", "Squash"), TestContext.Current.CancellationToken);
        Assert.Equal(expected, outcome);
    }

    [Fact]
    public async Task The_prompt_names_the_approval_and_a_cancelled_token_is_a_cancelled_prompt()
    {
        var presence = new FakePresence();
        var prompt = new DesktopBiometricPrompt(presence);
        await prompt.AuthenticateAsync(new BiometricRequest(Guid.NewGuid(), "abcdef0123456789", "Approve merge", "Squash acme/widgets #7"), TestContext.Current.CancellationToken);
        Assert.Contains("Squash acme/widgets #7", presence.Reasons[0]);
        Assert.Contains("abcdef01", presence.Reasons[0]);
        Assert.DoesNotContain("abcdef012", presence.Reasons[0]);

        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();
        Assert.Equal(BiometricOutcome.Cancelled, await prompt.AuthenticateAsync(new BiometricRequest(Guid.NewGuid(), "ab", "t", "s"), cts.Token));
    }

    [Fact]
    public async Task A_software_key_is_refused_and_signs_nothing()
    {
        using var platform = new FakePlatformSigner(ApprovalKeyCustody.Software);
        var signer = new DesktopDeviceApprovalSigner("dev", platform);

        await Assert.ThrowsAsync<InvalidOperationException>(async () => await signer.SignAsync(new byte[] { 1 }, TestContext.Current.CancellationToken));
        Assert.Equal(0, platform.Signed);
    }

    [Theory]
    [InlineData(ApprovalRefusal.NoTpm)]
    [InlineData(ApprovalRefusal.SoftwareKey)]
    public async Task An_unavailable_key_maps_to_a_failed_approval_without_leaking_the_payload(ApprovalRefusal reason)
    {
        using var platform = new FakePlatformSigner { Refuse = reason };
        var approver = new DeviceOwnerMergeApprover(
            new DesktopBiometricPrompt(new FakePresence()), new DesktopDeviceApprovalSigner("dev", platform), new Identity());

        var result = await approver.ApproveAsync(_request, TestContext.Current.CancellationToken);

        Assert.Equal(MergeApprovalOutcome.Failed, result.Outcome);
        Assert.Null(result.Merge);
    }

    [Fact]
    public async Task Errors_carry_the_reason_only_never_payload_or_signature_bytes()
    {
        var secret = new byte[] { 0xDE, 0xAD, 0xBE, 0xEF, 0x01, 0x02 };
        using var refusing = new FakePlatformSigner { Refuse = ApprovalRefusal.NoTpm };
        var ex1 = await Assert.ThrowsAsync<InvalidOperationException>(async () => await new DesktopDeviceApprovalSigner("dev", refusing).SignAsync(secret, TestContext.Current.CancellationToken));
        using var short_ = new FakePlatformSigner { ForcedSignature = secret };
        var ex2 = await Assert.ThrowsAsync<InvalidOperationException>(async () => await new DesktopDeviceApprovalSigner("dev", short_).SignAsync(secret, TestContext.Current.CancellationToken));

        foreach (var message in new[] { ex1.Message, ex2.Message })
        {
            Assert.DoesNotContain(Convert.ToHexString(secret), message, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain(Convert.ToBase64String(secret), message);
        }

        Assert.Contains("NoTpm", ex1.Message);
    }

    [Theory]
    [InlineData("")]
    [InlineData("has space")]
    [InlineData("bad/slash")]
    public void An_invalid_device_id_is_refused(string id)
    {
        using var platform = new FakePlatformSigner();
        Assert.Throws<ArgumentException>(() => new DesktopDeviceApprovalSigner(id, platform));
    }

    [Fact]
    public void A_device_id_longer_than_128_is_refused()
    {
        using var platform = new FakePlatformSigner();
        Assert.Throws<ArgumentException>(() => new DesktopDeviceApprovalSigner(new string('a', 129), platform));
        _ = new DesktopDeviceApprovalSigner(new string('a', 128), platform);
    }
}
