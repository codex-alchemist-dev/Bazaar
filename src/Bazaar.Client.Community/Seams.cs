using Bazaar.Contracts.Approvals;
using Bazaar.Contracts.Community;

namespace Bazaar.Client.Community;

/// <summary>
/// Signs an approval decision with the device key (doc 15, doc 32 section 5a). The head supplies the real signer; this
/// module only asks for a signature over the card it shows, so the signing code is not duplicated here.
/// </summary>
public interface IDecisionSigner
{
    /// <summary>
    /// Builds and signs the decision for <paramref name="card"/>. A null <see cref="ApprovalDecisionDto.SignatureBase64"/> in
    /// the result means the owner did not sign (for example a cancelled prompt), and nothing is sent.
    /// </summary>
    /// <param name="card">The approval card the decision answers.</param>
    /// <param name="decision">The owner's choice.</param>
    /// <param name="decidedAt">When the choice was made, from the client clock.</param>
    /// <param name="cancellationToken">Cancels the signing prompt.</param>
    Task<ApprovalDecisionDto> SignAsync(
        ApprovalCardDto card,
        ApprovalDecisionKind decision,
        DateTimeOffset decidedAt,
        CancellationToken cancellationToken);
}

/// <summary>Sends the owner's signed reply to a draft-review card to the server.</summary>
public interface IDraftReviewDecisionSender
{
    /// <summary>Sends the decision. Throws when the server refuses or cannot be reached.</summary>
    Task SendAsync(DraftReviewDecisionDto decision, CancellationToken cancellationToken);
}
