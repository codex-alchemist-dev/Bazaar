namespace Bazaar.GitHub.Adapter;

/// <summary>
/// Settings for the client merge route (doc 21, Merge route). The installation belongs to the operator's merge credential
/// (<c>github.merge_credential</c>), never to the client.
/// </summary>
public sealed class GitHubMergeOptions
{
    /// <summary>The App installation the merge credential acts through. Zero means not configured, and the route answers 502.</summary>
    public long InstallationId { get; init; }
}
