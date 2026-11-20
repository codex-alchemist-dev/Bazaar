using Bazaar.Contracts.Approvals;

namespace Bazaar.Client.Approvals.Tests;

public class ApprovalGateTests
{
    [Fact]
    public async Task Approve_after_a_fresh_biometric_signs_the_canonical_payload_and_sends_once()
    {
        using var rig = new Rig();
        var card = Rig.Card();

        var result = await rig.Gate.DecideAsync(Rig.Tap(card), CancellationToken.None);

        Assert.True(result.IsSent);
        var sent = Assert.Single(rig.Sender.Sent);
        Assert.Equal(card.ApprovalId, sent.ApprovalId);
        Assert.Equal(ApprovalDecisionKind.ApproveOnce, sent.Decision);
        Assert.Equal("device-1", sent.DeviceId);
        Assert.Equal(1, rig.Biometric.Calls);
        var payload = Assert.Single(rig.Signer.Signed);
        Assert.Equal(ApprovalSigningPayload.Build(card.ApprovalId, true, card.PayloadHash, card.ExpiresAt, "device-1"), payload);
        Assert.True(rig.Signer.Verify(payload, sent.SignatureBase64!));
        Assert.Equal(card.ApprovalId, Assert.Single(rig.Biometric.Requests).ApprovalId);
    }

    [Fact]
    public async Task Deny_is_signed_without_a_biometric_by_default()
    {
        using var rig = new Rig();

        var result = await rig.Gate.DecideAsync(Rig.Tap(Rig.Card(), ApprovalDecisionKind.Deny), CancellationToken.None);

        Assert.True(result.IsSent);
        Assert.Equal(0, rig.Biometric.Calls);
        Assert.Contains("\ndeny\n", System.Text.Encoding.UTF8.GetString(Assert.Single(rig.Signer.Signed)));
    }

    [Fact]
    public async Task Deny_asks_for_a_biometric_when_the_option_is_set()
    {
        using var rig = new Rig(new ApprovalGateOptions { RequireBiometricForDeny = true });
        rig.Biometric.Outcome = BiometricOutcome.Failed;

        var result = await rig.Gate.DecideAsync(Rig.Tap(Rig.Card(), ApprovalDecisionKind.Deny), CancellationToken.None);

        Assert.Equal(GateRefusal.BiometricFailed, result.Refusal);
        Assert.Equal(1, rig.Biometric.Calls);
        Assert.Empty(rig.Sender.Sent);
    }

    [Theory]
    [InlineData(BiometricOutcome.Cancelled, GateRefusal.BiometricCancelled)]
    [InlineData(BiometricOutcome.Failed, GateRefusal.BiometricFailed)]
    [InlineData(BiometricOutcome.Unavailable, GateRefusal.BiometricUnavailable)]
    public async Task A_biometric_that_does_not_succeed_signs_and_sends_nothing(BiometricOutcome outcome, GateRefusal expected)
    {
        using var rig = new Rig();
        rig.Biometric.Outcome = outcome;
        var card = Rig.Card();

        var result = await rig.Gate.DecideAsync(Rig.Tap(card), CancellationToken.None);

        Assert.Equal(expected, result.Refusal);
        Assert.Empty(rig.Signer.Signed);
        Assert.Empty(rig.Sender.Sent);

        // The approval is still open: a later fresh success works.
        rig.Biometric.Outcome = BiometricOutcome.Succeeded;
        Assert.True((await rig.Gate.DecideAsync(Rig.Tap(card), CancellationToken.None)).IsSent);
    }

    [Fact]
    public async Task A_prompt_that_throws_counts_as_a_failure()
    {
        using var rig = new Rig();
        rig.Biometric.Throws = true;

        var result = await rig.Gate.DecideAsync(Rig.Tap(Rig.Card()), CancellationToken.None);

        Assert.Equal(GateRefusal.BiometricFailed, result.Refusal);
        Assert.Empty(rig.Sender.Sent);
    }

    [Fact]
    public async Task A_cancelled_token_during_the_prompt_is_a_cancelled_prompt()
    {
        using var rig = new Rig();
        rig.Biometric.HoldNext();
        using var cts = new CancellationTokenSource();

        var pending = rig.Gate.DecideAsync(Rig.Tap(Rig.Card()), cts.Token).AsTask();
        await cts.CancelAsync();
        var result = await pending;

        Assert.Equal(GateRefusal.BiometricCancelled, result.Refusal);
        Assert.Empty(rig.Sender.Sent);
    }

    [Fact]
    public async Task A_grant_is_single_use()
    {
        using var rig = new Rig();
        var card = Rig.Card();
        var request = Rig.Tap(card);
        var granted = await rig.Gate.RequestBiometricAsync(request, CancellationToken.None);

        var first = await rig.Gate.SubmitAsync(request, granted.Grant, CancellationToken.None);
        var second = await rig.Gate.SubmitAsync(request, granted.Grant, CancellationToken.None);

        Assert.True(first.IsSent);
        Assert.False(second.IsSent);
        Assert.Equal(GateRefusal.AlreadyDecided, second.Refusal);
        Assert.Single(rig.Sender.Sent);
        Assert.Single(rig.Signer.Signed);
    }

    [Fact]
    public async Task A_grant_for_one_approval_cannot_sign_another()
    {
        using var rig = new Rig();
        var a = Rig.Card();
        var b = Rig.Card();
        var grantA = await rig.Gate.RequestBiometricAsync(Rig.Tap(a), CancellationToken.None);

        var result = await rig.Gate.SubmitAsync(Rig.Tap(b), grantA.Grant, CancellationToken.None);

        Assert.Equal(GateRefusal.GrantMismatch, result.Refusal);
        Assert.Empty(rig.Sender.Sent);
        // A's own grant is untouched and still works.
        Assert.True((await rig.Gate.SubmitAsync(Rig.Tap(a), grantA.Grant, CancellationToken.None)).IsSent);
    }

    [Fact]
    public async Task A_changed_content_hash_voids_the_grant()
    {
        using var rig = new Rig();
        var id = Guid.NewGuid();
        var shown = Rig.Card(id, Rig.Hash('a'));
        var changed = Rig.Card(id, Rig.Hash('b'));
        var grant = await rig.Gate.RequestBiometricAsync(Rig.Tap(shown), CancellationToken.None);

        var result = await rig.Gate.SubmitAsync(Rig.Tap(changed), grant.Grant, CancellationToken.None);

        Assert.Equal(GateRefusal.GrantMismatch, result.Refusal);
        Assert.Empty(rig.Sender.Sent);
        // The grant is gone for the original card too: a fresh biometric is needed.
        var again = await rig.Gate.SubmitAsync(Rig.Tap(shown), grant.Grant, CancellationToken.None);
        Assert.Equal(GateRefusal.GrantMissingOrReused, again.Refusal);
        Assert.Empty(rig.Sender.Sent);
    }

    [Fact]
    public async Task A_grant_for_approve_cannot_sign_a_different_decision()
    {
        using var rig = new Rig(new ApprovalGateOptions { RequireBiometricForDeny = true });
        var card = Rig.Card();
        var grant = await rig.Gate.RequestBiometricAsync(Rig.Tap(card), CancellationToken.None);

        var result = await rig.Gate.SubmitAsync(Rig.Tap(card, ApprovalDecisionKind.Deny), grant.Grant, CancellationToken.None);

        Assert.Equal(GateRefusal.GrantMismatch, result.Refusal);
        Assert.Empty(rig.Sender.Sent);
    }

    [Fact]
    public async Task An_expired_biometric_result_is_refused()
    {
        using var rig = new Rig();
        var card = Rig.Card();
        var grant = await rig.Gate.RequestBiometricAsync(Rig.Tap(card), CancellationToken.None);

        rig.Clock.Advance(TimeSpan.FromSeconds(ApprovalGateOptions.MaxBiometricGraceSeconds));
        var result = await rig.Gate.SubmitAsync(Rig.Tap(card), grant.Grant, CancellationToken.None);

        Assert.Equal(GateRefusal.GrantExpired, result.Refusal);
        Assert.Empty(rig.Sender.Sent);
        Assert.Empty(rig.Signer.Signed);
    }

    [Fact]
    public async Task A_result_just_inside_the_grace_period_is_accepted()
    {
        using var rig = new Rig();
        var card = Rig.Card();
        var grant = await rig.Gate.RequestBiometricAsync(Rig.Tap(card), CancellationToken.None);

        rig.Clock.Advance(TimeSpan.FromSeconds(ApprovalGateOptions.MaxBiometricGraceSeconds) - TimeSpan.FromMilliseconds(1));

        Assert.True((await rig.Gate.SubmitAsync(Rig.Tap(card), grant.Grant, CancellationToken.None)).IsSent);
    }

    [Theory]
    [InlineData(10_000, 30)]
    [InlineData(5, 5)]
    [InlineData(0, 1)]
    [InlineData(-4, 1)]
    public void The_grace_period_can_only_tighten(int configured, int effectiveSeconds)
    {
        var options = new ApprovalGateOptions { BiometricGraceSeconds = configured };

        Assert.Equal(TimeSpan.FromSeconds(effectiveSeconds), options.EffectiveBiometricGrace);
    }

    [Fact]
    public async Task A_tightened_grace_period_is_enforced()
    {
        using var rig = new Rig(new ApprovalGateOptions { BiometricGraceSeconds = 5 });
        var card = Rig.Card();
        var grant = await rig.Gate.RequestBiometricAsync(Rig.Tap(card), CancellationToken.None);

        rig.Clock.Advance(TimeSpan.FromSeconds(6));

        Assert.Equal(GateRefusal.GrantExpired, (await rig.Gate.SubmitAsync(Rig.Tap(card), grant.Grant, CancellationToken.None)).Refusal);
    }

    [Fact]
    public async Task A_forged_grant_object_is_refused()
    {
        using var rig = new Rig();
        var card = Rig.Card();
        // Open the approval so an entry exists, then present a grant the gate never issued.
        var real = await rig.Gate.RequestBiometricAsync(Rig.Tap(card), CancellationToken.None);
        var forged = new BiometricGrant(Guid.NewGuid(), card.ApprovalId, card.PayloadHash, ApprovalDecisionKind.ApproveOnce, rig.Clock.GetUtcNow(), rig.Clock.GetUtcNow().AddHours(1));

        var result = await rig.Gate.SubmitAsync(Rig.Tap(card), forged, CancellationToken.None);

        Assert.Equal(GateRefusal.GrantMissingOrReused, result.Refusal);
        Assert.Empty(rig.Sender.Sent);
        Assert.NotNull(real.Grant);
    }

    [Fact]
    public async Task Approve_without_a_grant_is_refused()
    {
        using var rig = new Rig();

        var result = await rig.Gate.SubmitAsync(Rig.Tap(Rig.Card()), null, CancellationToken.None);

        Assert.Equal(GateRefusal.BiometricRequired, result.Refusal);
        Assert.Empty(rig.Signer.Signed);
    }

    [Fact]
    public async Task A_signed_decision_is_never_replayed()
    {
        using var rig = new Rig();
        var card = Rig.Card();
        Assert.True((await rig.Gate.DecideAsync(Rig.Tap(card), CancellationToken.None)).IsSent);

        var approveAgain = await rig.Gate.DecideAsync(Rig.Tap(card), CancellationToken.None);
        var denyAfter = await rig.Gate.DecideAsync(Rig.Tap(card, ApprovalDecisionKind.Deny), CancellationToken.None);

        Assert.Equal(GateRefusal.AlreadyDecided, approveAgain.Refusal);
        Assert.Equal(GateRefusal.AlreadyDecided, denyAfter.Refusal);
        Assert.Single(rig.Sender.Sent);
        Assert.Equal(1, rig.Biometric.Calls);
    }

    [Fact]
    public async Task Concurrent_double_approve_signs_and_sends_once()
    {
        using var rig = new Rig();
        var card = Rig.Card();
        rig.Biometric.HoldNext();

        var first = rig.Gate.DecideAsync(Rig.Tap(card), CancellationToken.None).AsTask();
        var second = await rig.Gate.DecideAsync(Rig.Tap(card), CancellationToken.None);
        rig.Biometric.Release();
        var firstResult = await first;

        Assert.Equal(GateRefusal.AlreadyInProgress, second.Refusal);
        Assert.True(firstResult.IsSent);
        Assert.Single(rig.Sender.Sent);
        Assert.Equal(1, rig.Biometric.Calls);
    }

    [Fact]
    public async Task Many_concurrent_approves_send_exactly_one()
    {
        using var rig = new Rig();
        var card = Rig.Card();

        var results = await Task.WhenAll(Enumerable.Range(0, 32).Select(_ => Task.Run(() => rig.Gate.DecideAsync(Rig.Tap(card), CancellationToken.None).AsTask())));

        Assert.Equal(1, results.Count(r => r.IsSent));
        Assert.Single(rig.Sender.Sent);
        Assert.Single(rig.Signer.Signed);
    }

    [Fact]
    public async Task Approve_and_deny_racing_send_at_most_one_decision()
    {
        using var rig = new Rig();
        var card = Rig.Card();
        rig.Biometric.HoldNext();

        var approve = rig.Gate.DecideAsync(Rig.Tap(card), CancellationToken.None).AsTask();
        var deny = await rig.Gate.DecideAsync(Rig.Tap(card, ApprovalDecisionKind.Deny), CancellationToken.None);
        rig.Biometric.Release();
        await approve;

        Assert.False(deny.IsSent);
        Assert.Single(rig.Sender.Sent);
    }

    [Theory]
    [InlineData(ApprovalInputSource.Dictation)]
    [InlineData(ApprovalInputSource.Other)]
    public async Task Only_a_card_tap_can_decide(ApprovalInputSource source)
    {
        using var rig = new Rig();
        var card = Rig.Card();

        foreach (var kind in new[] { ApprovalDecisionKind.ApproveOnce, ApprovalDecisionKind.Deny })
        {
            var result = await rig.Gate.DecideAsync(new ApprovalDecisionRequest(card, kind, source), CancellationToken.None);
            Assert.Equal(GateRefusal.NotFromCard, result.Refusal);
        }

        Assert.Equal(0, rig.Biometric.Calls);
        Assert.Empty(rig.Signer.Signed);
        Assert.Empty(rig.Sender.Sent);
    }

    [Theory]
    [InlineData(ApprovalDecisionKind.EditThenApprove)]
    [InlineData(ApprovalDecisionKind.ApproveForTask)]
    [InlineData((ApprovalDecisionKind)99)]
    public async Task Decisions_the_phone_cannot_sign_are_refused_before_any_prompt(ApprovalDecisionKind kind)
    {
        using var rig = new Rig();
        var card = Rig.Card(decisions: [kind]);

        var result = await rig.Gate.DecideAsync(Rig.Tap(card, kind), CancellationToken.None);

        Assert.Equal(GateRefusal.UnsupportedDecision, result.Refusal);
        Assert.Equal(0, rig.Biometric.Calls);
    }

    [Fact]
    public async Task Cards_that_are_not_pending_expired_or_offering_the_decision_are_refused_before_any_prompt()
    {
        using var rig = new Rig();

        Assert.Equal(GateRefusal.CardNotPending, (await rig.Gate.DecideAsync(Rig.Tap(Rig.Card(status: ApprovalCardStatus.Approved)), CancellationToken.None)).Refusal);
        Assert.Equal(GateRefusal.CardExpired, (await rig.Gate.DecideAsync(Rig.Tap(Rig.Card(expiresAt: Rig.Start)), CancellationToken.None)).Refusal);
        Assert.Equal(GateRefusal.DecisionNotAvailable, (await rig.Gate.DecideAsync(Rig.Tap(Rig.Card(decisions: [ApprovalDecisionKind.Deny])), CancellationToken.None)).Refusal);
        Assert.Equal(GateRefusal.InvalidCard, (await rig.Gate.DecideAsync(Rig.Tap(Rig.Card(hash: "ABC\nDEF")), CancellationToken.None)).Refusal);
        Assert.Equal(0, rig.Biometric.Calls);
        Assert.Empty(rig.Sender.Sent);
    }

    [Fact]
    public async Task A_card_that_expires_during_the_prompt_is_refused_and_leaves_no_live_grant()
    {
        using var rig = new Rig();
        var card = Rig.Card(expiresAt: Rig.Start.AddSeconds(10));
        rig.Biometric.HoldNext();

        var pending = rig.Gate.DecideAsync(Rig.Tap(card), CancellationToken.None).AsTask();
        rig.Clock.Advance(TimeSpan.FromSeconds(11));
        rig.Biometric.Release();
        var result = await pending;

        Assert.Equal(GateRefusal.CardExpired, result.Refusal);
        Assert.Empty(rig.Sender.Sent);
        Assert.Empty(rig.Signer.Signed);
    }

    [Fact]
    public async Task A_signer_failure_signs_nothing_and_the_approval_stays_open()
    {
        using var rig = new Rig();
        var card = Rig.Card();
        rig.Signer.Fails = true;

        var result = await rig.Gate.DecideAsync(Rig.Tap(card), CancellationToken.None);

        Assert.Equal(GateRefusal.SigningFailed, result.Refusal);
        Assert.Empty(rig.Sender.Sent);

        rig.Signer.Fails = false;
        Assert.True((await rig.Gate.DecideAsync(Rig.Tap(card), CancellationToken.None)).IsSent);
        Assert.Equal(2, rig.Biometric.Calls);
    }

    [Fact]
    public async Task A_signature_of_the_wrong_length_is_never_sent()
    {
        using var rig = new Rig();
        rig.Signer.BadLength = 32;

        var result = await rig.Gate.DecideAsync(Rig.Tap(Rig.Card()), CancellationToken.None);

        Assert.Equal(GateRefusal.SigningFailed, result.Refusal);
        Assert.Empty(rig.Sender.Sent);
    }

    [Fact]
    public async Task A_failed_send_is_reported_and_not_retried()
    {
        using var rig = new Rig();
        var card = Rig.Card();
        rig.Sender.Fails = true;

        var result = await rig.Gate.DecideAsync(Rig.Tap(card), CancellationToken.None);
        rig.Sender.Fails = false;
        var retry = await rig.Gate.DecideAsync(Rig.Tap(card), CancellationToken.None);

        Assert.Equal(GateRefusal.SendFailed, result.Refusal);
        Assert.False(result.IsSent);
        Assert.Equal(GateRefusal.AlreadyDecided, retry.Refusal);
        Assert.Empty(rig.Sender.Sent);
    }

    [Fact]
    public async Task A_discarded_grant_cannot_be_used()
    {
        using var rig = new Rig();
        var card = Rig.Card();
        var grant = await rig.Gate.RequestBiometricAsync(Rig.Tap(card), CancellationToken.None);

        rig.Gate.Discard(grant.Grant!);
        var result = await rig.Gate.SubmitAsync(Rig.Tap(card), grant.Grant, CancellationToken.None);

        Assert.Equal(GateRefusal.GrantMissingOrReused, result.Refusal);
        Assert.Empty(rig.Sender.Sent);
    }

    [Fact]
    public void A_signer_with_a_bad_device_id_is_rejected_up_front()
    {
        using var signer = new FakeSigner { DeviceId = "bad\nid" };

        Assert.Throws<ArgumentException>(() => new ApprovalGate(new FakeBiometric(), signer, new RecordingSender()));
    }

    [Fact]
    public void The_signed_message_matches_the_servers_layout()
    {
        var id = Guid.NewGuid();
        var hash = Rig.Hash('c');
        var expiresAt = Rig.Start.AddHours(2);

        var client = ApprovalSigningPayload.Build(id, true, hash, expiresAt, "device-1");
        var server = Bazaar.Server.Security.Approvals.ApprovalSignature.Message(
            id, Bazaar.Server.Security.Approvals.ApprovalDecision.Approve, Convert.FromHexString(hash), expiresAt, "device-1");
        var clientDeny = ApprovalSigningPayload.Build(id, false, hash, expiresAt, "device-1");
        var serverDeny = Bazaar.Server.Security.Approvals.ApprovalSignature.Message(
            id, Bazaar.Server.Security.Approvals.ApprovalDecision.Deny, Convert.FromHexString(hash), expiresAt, "device-1");

        Assert.Equal(server, client);
        Assert.Equal(serverDeny, clientDeny);
    }

    [Fact]
    public async Task The_servers_verifier_accepts_what_the_gate_signs()
    {
        using var rig = new Rig();
        var card = Rig.Card();
        await rig.Gate.DecideAsync(Rig.Tap(card), CancellationToken.None);
        var sent = Assert.Single(rig.Sender.Sent);

        var message = Bazaar.Server.Security.Approvals.ApprovalSignature.Message(
            card.ApprovalId, Bazaar.Server.Security.Approvals.ApprovalDecision.Approve, Convert.FromHexString(card.PayloadHash), card.ExpiresAt, sent.DeviceId);

        Assert.True(Bazaar.Server.Security.Approvals.ApprovalSignature.Verify(rig.Signer.PublicKeySpki, message, Convert.FromBase64String(sent.SignatureBase64!)));
    }

    [Theory]
    [InlineData("0000")]
    [InlineData("ABCDEF0123456789ABCDEF0123456789")]
    public void Content_hash_shapes_that_could_smuggle_a_line_are_not_signable(string hash)
    {
        Assert.Throws<ArgumentException>(() => ApprovalSigningPayload.Build(Guid.NewGuid(), true, hash, Rig.Start, "device-1"));
    }
}
