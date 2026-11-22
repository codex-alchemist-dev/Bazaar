using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace Bazaar.Client.PullRequests;

/// <summary>
/// The client copy of the server's <c>OwnerMergeArgs.Hash</c> (doc 21, Merge request path): SHA-256 over the PR and the merge
/// request, each field length-prefixed so no two merges share one hash. The phone cannot reference the server assembly, so
/// the layout is repeated here and a test pins it to the server's result.
/// </summary>
public static class PrMergeArgs
{
    /// <summary>The args hash that the owner payload carries for this exact merge.</summary>
    public static byte[] Hash(PrIdentity pr, string headSha, string commitTitle, string commitMessage)
    {
        ArgumentNullException.ThrowIfNull(pr);
        var sb = new StringBuilder();
        Append(sb, pr.Owner);
        Append(sb, pr.Repo);
        Append(sb, pr.Number.ToString(CultureInfo.InvariantCulture));
        Append(sb, headSha);
        Append(sb, commitTitle);
        Append(sb, commitMessage);
        return SHA256.HashData(Encoding.UTF8.GetBytes(sb.ToString()));
    }

    private static void Append(StringBuilder sb, string value) => sb.Append(value.Length).Append(':').Append(value).Append(';');
}
