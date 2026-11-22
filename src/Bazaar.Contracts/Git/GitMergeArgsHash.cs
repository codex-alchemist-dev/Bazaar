using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace Bazaar.Contracts.Git;

/// <summary>
/// The args hash an owner signs for a squash merge (doc 32 section 5a, doc 52). It lives in Contracts so the client that builds
/// the approval and the server gate that checks it share one definition and cannot drift.
/// </summary>
public static class GitMergeArgsHash
{
    /// <summary>The action name the owner payload carries for a squash merge.</summary>
    public const string Action = "pr.merge";

    /// <summary>
    /// SHA-256 over the provider, repo, PR number, head sha, commit title and sign-off, each field length-prefixed so no two
    /// merges share one hash. The head sha is lower-cased, so the case a caller sends does not change the hash.
    /// </summary>
    public static byte[] Compute(string provider, string repoFullName, long number, string headSha, string commitTitle, string signOff)
    {
        ArgumentNullException.ThrowIfNull(provider);
        ArgumentNullException.ThrowIfNull(repoFullName);
        ArgumentNullException.ThrowIfNull(headSha);
        ArgumentNullException.ThrowIfNull(commitTitle);
        ArgumentNullException.ThrowIfNull(signOff);

        var sb = new StringBuilder();
        Append(sb, provider);
        Append(sb, repoFullName);
        Append(sb, number.ToString(CultureInfo.InvariantCulture));
        Append(sb, headSha.ToLowerInvariant());
        Append(sb, commitTitle);
        Append(sb, signOff);
        return SHA256.HashData(Encoding.UTF8.GetBytes(sb.ToString()));
    }

    private static void Append(StringBuilder sb, string value) =>
        sb.Append(value.Length.ToString(CultureInfo.InvariantCulture)).Append(':').Append(value).Append(';');
}
