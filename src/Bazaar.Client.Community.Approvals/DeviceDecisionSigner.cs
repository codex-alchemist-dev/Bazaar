using Bazaar.Client.Approvals;
using Bazaar.Contracts.Approvals;

namespace Bazaar.Client.Community.Approvals;

/// <summary>
/// <see cref="IDecisionSigner"/> over the client approvals module (doc 15 flow step 4, doc 32 section 5a). It signs the same
/// <c>bazaar.approval.v1</c> message the server verifies, with the enrolled device key behind
/// <see cref="IDeviceApprovalSigner"/>, so no signing format or key handling is invented here. The key never leaves the
/// signer: this class only passes bytes in and takes a signature out.
/// </summary>
/// <remarks>
/// Approving (approve once, edit then approve) needs a fresh biometric success for this exact card, like the phone's approval
/// gate. Deny needs none. A refused, cancelled or failed prompt, an expired or non-pending card, or a failing signer yields a
/// decision with a null signature, which is never sent. The signed bit is "approve" only for
/// <see cref="ApprovalDecisionKind.ApproveOnce"/>; the other kinds sign "deny", as the approval gate does, because an edited
/// body is a new draft that needs its own approval (doc 36).
/// </remarks>
public sealed class DeviceDecisionSigner : IDecisionSigner
{
    private readonly IBiometricPrompt _biometric;
    private readonly IDeviceApprovalSigner _signer;

    /// <summary>Creates the signer. The device id must have the allowed shape.</summary>
    public DeviceDecisionSigner(IBiometricPrompt biometric, IDeviceApprovalSigner signer)
    {
        _biometric = biometric ?? throw new ArgumentNullException(nameof(biometric));
        _signer = signer ?? throw new ArgumentNullException(nameof(signer));
        if (!ApprovalSigningPayload.IsValidDeviceId(_signer.DeviceId))
        {
            throw new ArgumentException("The signer's device id has a disallowed shape.", nameof(signer));
        }
    }

    /// <inheritdoc />
    public async Task<ApprovalDecisionDto> SignAsync(
        ApprovalCardDto card,
        ApprovalDecisionKind decision,
        DateTimeOffset decidedAt,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(card);

        if (decision is not (ApprovalDecisionKind.ApproveOnce or ApprovalDecisionKind.Deny or ApprovalDecisionKind.EditThenApprove)
            || card.Status != ApprovalCardStatus.Pending
            || decidedAt >= card.ExpiresAt
            || !card.AvailableDecisions.Contains(decision)
            || !ApprovalSigningPayload.IsValidContentHash(card.PayloadHash))
        {
            return Unsigned(card, decision, decidedAt);
        }

        if (decision != ApprovalDecisionKind.Deny)
        {
            BiometricOutcome outcome;
            try
            {
                var prompt = new BiometricRequest(card.ApprovalId, card.PayloadHash, "Confirm approval", $"{card.AgentId}: {card.Tool}");
                outcome = await _biometric.AuthenticateAsync(prompt, cancellationToken).ConfigureAwait(false);
            }
#pragma warning disable CA1031 // Any prompt failure or cancellation means no biometric success; the reason is not trusted detail.
            catch (Exception)
#pragma warning restore CA1031
            {
                return Unsigned(card, decision, decidedAt);
            }

            if (outcome != BiometricOutcome.Succeeded)
            {
                return Unsigned(card, decision, decidedAt);
            }
        }

        try
        {
            var payload = ApprovalSigningPayload.Build(
                card.ApprovalId, decision == ApprovalDecisionKind.ApproveOnce, card.PayloadHash, card.ExpiresAt, _signer.DeviceId);
            var signature = await _signer.SignAsync(payload, cancellationToken).ConfigureAwait(false);
            if (signature is null || signature.Length != ApprovalSigningPayload.SignatureLength)
            {
                return Unsigned(card, decision, decidedAt);
            }

            return new ApprovalDecisionDto(card.ApprovalId, decision, _signer.DeviceId, Convert.ToBase64String(signature), decidedAt);
        }
#pragma warning disable CA1031 // A failing signer means nothing was signed; the card stays open for a fresh attempt.
        catch (Exception)
#pragma warning restore CA1031
        {
            return Unsigned(card, decision, decidedAt);
        }
    }

    private ApprovalDecisionDto Unsigned(ApprovalCardDto card, ApprovalDecisionKind decision, DateTimeOffset decidedAt) =>
        new(card.ApprovalId, decision, _signer.DeviceId, null, decidedAt);
}
