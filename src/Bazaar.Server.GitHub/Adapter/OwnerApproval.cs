using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Bazaar.Approvals;
using Bazaar.Contracts.GitHub;

namespace Bazaar.Server.GitHub.Adapter;

/// <summary>
/// The owner's signed approval of one squash merge (doc 21, doc 32 section 5a). The payload is the shared owner payload:
/// action <c>pr.merge</c>, the args hash of this exact merge, and the PR head sha. It is verified by the shared
/// <see cref="IOwnerSignatureVerifier"/>, so there is one owner-signature check for every gated action.
/// </summary>
public sealed record OwnerMergeApproval(PrRef Pr, OwnerPayload Payload, byte[] Signature);

/// <summary>What an owner signs for a merge: the action name and a hash that binds the PR and the merge request.</summary>
public static class OwnerMergeArgs
{
    /// <summary>The action name the owner payload carries for a squash merge.</summary>
    public const string Action = "pr.merge";

    /// <summary>SHA-256 over the PR and the merge request, each field length-prefixed so no two merges share one hash.</summary>
    public static byte[] Hash(PrRef pr, MergeRequest merge)
    {
        var sb = new StringBuilder();
        Append(sb, pr.Owner);
        Append(sb, pr.Repo);
        Append(sb, pr.Number.ToString(CultureInfo.InvariantCulture));
        Append(sb, merge.HeadSha);
        Append(sb, merge.CommitTitle);
        Append(sb, merge.CommitMessage);
        return SHA256.HashData(Encoding.UTF8.GetBytes(sb.ToString()));
    }

    private static void Append(StringBuilder sb, string value) => sb.Append(value.Length).Append(':').Append(value).Append(';');
}

/// <summary>Why <see cref="OwnerSignedMerge"/> refused a merge, so a caller can answer without parsing a message.</summary>
public enum MergeRefusalReason
{
    /// <summary>The approval and the merge name different head shas.</summary>
    HeadMismatch,

    /// <summary>The approval is not for <c>pr.merge</c>.</summary>
    WrongAction,

    /// <summary>The approval's args hash is for a different merge.</summary>
    DifferentMerge,

    /// <summary>The owner signature verifier refused (forged, expired, unknown or revoked device, or a replayed nonce).</summary>
    SignatureRejected,

    /// <summary>The PR head moved after approval.</summary>
    HeadMoved,

    /// <summary>Checks at the approved head are not passing.</summary>
    ChecksNotPassing,
}

/// <summary>A squash merge was refused because its owner approval did not hold. No merge request was sent.</summary>
public sealed class MergeNotApprovedException : Exception
{
    public MergeNotApprovedException(string reason)
        : this(reason, MergeRefusalReason.DifferentMerge)
    {
    }

    public MergeNotApprovedException(string reason, MergeRefusalReason refusal, OwnerSignatureVerdict? verdict = null, PrChecksRollup? checks = null)
        : base($"The squash merge was refused: {reason}.")
    {
        Reason = refusal;
        Verdict = verdict;
        Checks = checks;
    }

    /// <summary>The checks rollup when <see cref="Reason"/> is <see cref="MergeRefusalReason.ChecksNotPassing"/>.</summary>
    public PrChecksRollup? Checks { get; }

    /// <summary>The check that failed.</summary>
    public MergeRefusalReason Reason { get; }

    /// <summary>The verifier's verdict when <see cref="Reason"/> is <see cref="MergeRefusalReason.SignatureRejected"/>.</summary>
    public OwnerSignatureVerdict? Verdict { get; }
}
