using Bazaar.Contracts.GitHub;

namespace Bazaar.Client.PullRequests;

/// <summary>Where a merge request came from. Only a card-style tap can authorise a merge; dictated text never can.</summary>
public enum OwnerActionSource
{
    /// <summary>A deliberate tap on the merge control.</summary>
    Button,

    /// <summary>Dictated or voice input. Always refused.</summary>
    Dictation,

    /// <summary>Anything else, such as a programmatic call. Always refused.</summary>
    Other,
}

/// <summary>The merge the owner is being asked to approve. It names the PR, the head sha and the exact squash message.</summary>
/// <param name="Source">Where the request came from.</param>
/// <param name="Pr">The PR identity.</param>
/// <param name="HeadSha">The head sha the owner is looking at. A new head makes the approval void.</param>
/// <param name="CommitTitle">Squash commit title.</param>
/// <param name="CommitMessage">Squash commit message, sign-off line included when the owner adds one.</param>
public sealed record OwnerMergeIntent(OwnerActionSource Source, PrIdentity Pr, string HeadSha, string CommitTitle, string CommitMessage);

/// <summary>Why the owner's approval was not given.</summary>
public enum OwnerActionRefusal
{
    /// <summary>The request did not come from a tap.</summary>
    NotFromButton,

    /// <summary>Merging is off on this device (<c>android.merge_from_folded</c>).</summary>
    MergeDisabled,

    /// <summary>The request is malformed (identity, head sha or title).</summary>
    InvalidRequest,

    /// <summary>The person dismissed the biometric prompt.</summary>
    BiometricCancelled,

    /// <summary>The biometric check failed or was locked out.</summary>
    BiometricFailed,

    /// <summary>No biometric is enrolled. There is no weaker fallback.</summary>
    BiometricUnavailable,

    /// <summary>The biometric succeeded but the grace period ran out before signing.</summary>
    BiometricExpired,

    /// <summary>The device key did not sign.</summary>
    SigningFailed,
}

/// <summary>A signed owner approval for one merge at one head sha. Single use: see <see cref="IOwnerActionGate.TrySpend"/>.</summary>
/// <param name="ApprovalId">Unique id of this approval.</param>
/// <param name="HeadSha">The head sha it approves.</param>
/// <param name="Payload">The owner payload as it travels to the server.</param>
/// <param name="Signature">The device signature over the exact payload bytes.</param>
public sealed record OwnerSignedAction(Guid ApprovalId, string HeadSha, PrOwnerPayload Payload, byte[] Signature);

/// <summary>The outcome of asking for an owner approval. Exactly one of the two is set.</summary>
/// <param name="Refusal">Why nothing was signed, or null.</param>
/// <param name="Approval">The signed approval, or null when refused.</param>
public sealed record OwnerActionResult(OwnerActionRefusal? Refusal, OwnerSignedAction? Approval);

/// <summary>
/// The owner-signed action gate (doc 29 Approvals, doc 32 section 5a). Today it authorises one action, the squash merge
/// (<c>pr.merge</c>). Every approval needs a fresh biometric check; nothing is cached across heads.
/// </summary>
public interface IOwnerActionGate
{
    /// <summary>Shows a fresh biometric prompt for this merge and, on success, signs one approval for it.</summary>
    ValueTask<OwnerActionResult> AuthorizeMergeAsync(OwnerMergeIntent intent, CancellationToken cancellationToken);

    /// <summary>
    /// Marks a signed approval as used for sending. Returns false when it was already used, so a replay is refused.
    /// Call it once, before the merge request is sent.
    /// </summary>
    bool TrySpend(OwnerSignedAction approval);
}
