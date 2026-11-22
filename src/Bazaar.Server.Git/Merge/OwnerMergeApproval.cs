using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Bazaar.Approvals;

namespace Bazaar.Server.Git.Merge;

/// <summary>
/// The owner's signed approval of one squash merge (doc 32 section 5a, doc 52). The payload is the shared owner payload:
/// action <see cref="GitMergeArgs.Action"/>, the args hash of this exact merge, and the PR head sha.
/// </summary>
public sealed record OwnerMergeApproval(PrRef Pr, OwnerPayload Payload, byte[] Signature);

/// <summary>What an owner signs for a merge: the action name and a hash that binds the PR and the merge request.</summary>
public static class GitMergeArgs
{
    /// <summary>The action name the owner payload carries for a squash merge.</summary>
    public const string Action = "pr.merge";

    /// <summary>
    /// SHA-256 over the provider, repo, PR number and merge request, each field length-prefixed so no two merges share one hash.
    /// The head sha is lower-cased, so the case a client sends does not change the hash.
    /// </summary>
    public static byte[] Hash(PrRef pr, MergeRequest merge)
    {
        ArgumentNullException.ThrowIfNull(pr);
        ArgumentNullException.ThrowIfNull(merge);
        var sb = new StringBuilder();
        Append(sb, pr.Provider);
        Append(sb, pr.FullName);
        Append(sb, pr.Number.ToString(CultureInfo.InvariantCulture));
        Append(sb, merge.HeadSha.ToLowerInvariant());
        Append(sb, merge.CommitTitle);
        Append(sb, merge.SignOff);
        return SHA256.HashData(Encoding.UTF8.GetBytes(sb.ToString()));
    }

    private static void Append(StringBuilder sb, string value) =>
        sb.Append(value.Length.ToString(CultureInfo.InvariantCulture)).Append(':').Append(value).Append(';');
}

/// <summary>A squash merge was refused because its owner approval or the PR state did not hold. No merge request was sent.</summary>
public sealed class MergeNotApprovedException : Exception
{
    public MergeNotApprovedException(string reason, string code = "not_approved", string? currentHeadSha = null)
        : base($"The squash merge was refused: {reason}.")
    {
        Reason = reason;
        Code = code;
        CurrentHeadSha = currentHeadSha;
    }

    /// <summary>The forge's current head sha, set only for <c>head_moved</c>.</summary>
    public string? CurrentHeadSha { get; }

    /// <summary>The short human reason, without the sentence frame.</summary>
    public string Reason { get; }

    /// <summary>A stable machine code: <c>head_mismatch</c>, <c>wrong_action</c>, <c>args_mismatch</c>, <c>bad_signature</c>, <c>head_moved</c>, <c>checks_not_green</c>.</summary>
    public string Code { get; }
}
