using Bazaar.DeviceApproval;

namespace Bazaar.DeviceApproval.Tests;

/// <summary>The platform-neutral gate, with fakes: no TPM and no Hello are needed for these.</summary>
public sealed class ApprovalGateTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static ReadOnlyMemory<byte> Payload => "merge pr 685 head abc"u8.ToArray();

    [Fact]
    public async Task A_software_key_is_refused_before_presence_is_asked()
    {
        var signer = new FakeSigner(ApprovalKeyCustody.Software);
        var presence = new FakePresence(PresenceOutcome.Verified);
        var gate = new ApprovalGate(signer, presence);

        var ex = await Assert.ThrowsAsync<ApprovalRefusedException>(() => gate.ApproveAsync(Payload, "approve", Ct).AsTask());

        Assert.Equal(ApprovalRefusal.SoftwareKey, ex.Reason);
        Assert.Equal(0, presence.Asks);
        Assert.Equal(0, signer.Signs);
    }

    [Theory]
    [InlineData(PresenceOutcome.Cancelled, ApprovalRefusal.PresenceCancelled)]
    [InlineData(PresenceOutcome.Unavailable, ApprovalRefusal.PresenceUnavailable)]
    [InlineData(PresenceOutcome.Failed, ApprovalRefusal.PresenceFailed)]
    public async Task Any_presence_failure_refuses_and_signs_nothing(PresenceOutcome outcome, ApprovalRefusal expected)
    {
        var signer = new FakeSigner(ApprovalKeyCustody.Tpm);
        var gate = new ApprovalGate(signer, new FakePresence(outcome));

        var ex = await Assert.ThrowsAsync<ApprovalRefusedException>(() => gate.ApproveAsync(Payload, "approve", Ct).AsTask());

        Assert.Equal(expected, ex.Reason);
        Assert.Equal(0, signer.Signs);
    }

    [Fact]
    public async Task A_tpm_key_and_verified_presence_produce_a_signature()
    {
        var signer = new FakeSigner(ApprovalKeyCustody.Tpm);
        var gate = new ApprovalGate(signer, new FakePresence(PresenceOutcome.Verified));

        var approval = await gate.ApproveAsync(Payload, "approve", Ct);

        Assert.Equal(ApprovalKeyCustody.Tpm, approval.Custody);
        Assert.Equal(1, signer.Signs);
    }

    [Fact]
    public async Task Each_approval_asks_for_presence_again()
    {
        var presence = new FakePresence(PresenceOutcome.Verified);
        var gate = new ApprovalGate(new FakeSigner(ApprovalKeyCustody.Tpm), presence);

        await gate.ApproveAsync(Payload, "one", Ct);
        await gate.ApproveAsync(Payload, "two", Ct);

        Assert.Equal(2, presence.Asks);
    }

    private sealed class FakeSigner(ApprovalKeyCustody custody) : IApprovalSigner
    {
        public ApprovalKeyCustody Custody { get; } = custody;

        public int Signs { get; private set; }

        public ValueTask<ApprovalSignature> SignAsync(ReadOnlyMemory<byte> payload, CancellationToken cancellationToken)
        {
            Signs++;
            return ValueTask.FromResult(new ApprovalSignature(new byte[64], "fake", Custody));
        }
    }

    private sealed class FakePresence(PresenceOutcome outcome) : IPresenceVerifier
    {
        public int Asks { get; private set; }

        public ValueTask<PresenceOutcome> VerifyAsync(string reason, CancellationToken cancellationToken)
        {
            Asks++;
            return ValueTask.FromResult(outcome);
        }
    }
}
