using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Bazaar.Server.Approvals;

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

/// <summary>A squash merge was refused because its owner approval did not hold. No merge request was sent.</summary>
public sealed class MergeNotApprovedException : Exception
{
    public MergeNotApprovedException(string reason)
        : base($"The squash merge was refused: {reason}.")
    {
    }
}
