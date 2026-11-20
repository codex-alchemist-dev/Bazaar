using Bazaar.Contracts.Approvals;
using Microsoft.Extensions.Time.Testing;

namespace Bazaar.Client.Approvals.Tests;

public class PlatformSeamsTests
{
    [Theory]
    [InlineData(5, BiometricOutcome.Cancelled)]
    [InlineData(10, BiometricOutcome.Cancelled)]
    [InlineData(13, BiometricOutcome.Cancelled)]
    [InlineData(1, BiometricOutcome.Unavailable)]
    [InlineData(11, BiometricOutcome.Unavailable)]
    [InlineData(12, BiometricOutcome.Unavailable)]
    [InlineData(14, BiometricOutcome.Unavailable)]
    [InlineData(7, BiometricOutcome.Failed)]
    [InlineData(9, BiometricOutcome.Failed)]
    [InlineData(3, BiometricOutcome.Failed)]
    [InlineData(-1, BiometricOutcome.Failed)]
    [InlineData(9999, BiometricOutcome.Failed)]
    public void An_error_code_is_never_a_success(int code, BiometricOutcome expected)
    {
        var outcome = BiometricErrorMap.FromErrorCode(code);

        Assert.Equal(expected, outcome);
        Assert.NotEqual(BiometricOutcome.Succeeded, outcome);
    }

    [Fact]
    public void The_not_enrolled_signer_has_a_valid_device_id_and_reports_not_enrolled()
    {
        var signer = new NotEnrolledApprovalSigner();

        Assert.False(signer.IsEnrolled);
        Assert.True(ApprovalSigningPayload.IsValidDeviceId(signer.DeviceId));
    }

    [Fact]
    public async Task The_not_enrolled_signer_never_signs()
    {
        await Assert.ThrowsAsync<ApprovalKeyNotEnrolledException>(
            async () => await new NotEnrolledApprovalSigner().SignAsync(new byte[] { 1 }, CancellationToken.None));
    }

    [Fact]
    public async Task Approve_on_a_not_enrolled_device_shows_no_prompt_and_sends_nothing()
    {
        var inner = new FakeBiometric();
        var sender = new RecordingSender();
        var signer = new NotEnrolledApprovalSigner();
        var gate = new ApprovalGate(new SignerReadyBiometricPrompt(inner, () => signer.IsEnrolled), signer, sender, clock: new FakeTimeProvider(Rig.Start));

        var result = await gate.DecideAsync(Rig.Tap(Rig.Card()), CancellationToken.None);

        Assert.False(result.IsSent);
        Assert.Equal(GateRefusal.BiometricUnavailable, result.Refusal);
        Assert.Equal(0, inner.Calls);
        Assert.Empty(sender.Sent);
    }

    [Fact]
    public async Task Deny_on_a_not_enrolled_device_fails_to_sign_and_sends_nothing()
    {
        var sender = new RecordingSender();
        var gate = new ApprovalGate(new FakeBiometric(), new NotEnrolledApprovalSigner(), sender, clock: new FakeTimeProvider(Rig.Start));

        var result = await gate.DecideAsync(Rig.Tap(Rig.Card(), ApprovalDecisionKind.Deny), CancellationToken.None);

        Assert.Equal(GateRefusal.SigningFailed, result.Refusal);
        Assert.Empty(sender.Sent);
    }

    [Fact]
    public async Task The_prompt_is_shown_when_the_signer_can_sign()
    {
        var inner = new FakeBiometric();
        var prompt = new SignerReadyBiometricPrompt(inner, () => true);

        var outcome = await prompt.AuthenticateAsync(new BiometricRequest(Guid.NewGuid(), Rig.Hash(), "t", "s"), CancellationToken.None);

        Assert.Equal(BiometricOutcome.Succeeded, outcome);
        Assert.Equal(1, inner.Calls);
    }

    [Fact]
    public async Task The_unrouted_sender_fails_so_the_gate_reports_a_failed_send()
    {
        using var signer = new FakeSigner();
        var gate = new ApprovalGate(new FakeBiometric(), signer, new UnroutedApprovalDecisionSender(), clock: new FakeTimeProvider(Rig.Start));

        var result = await gate.DecideAsync(Rig.Tap(Rig.Card()), CancellationToken.None);

        Assert.Equal(GateRefusal.SendFailed, result.Refusal);
    }
}
