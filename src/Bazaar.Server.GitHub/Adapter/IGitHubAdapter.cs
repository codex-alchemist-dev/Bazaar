namespace Bazaar.Server.GitHub.Adapter;

/// <summary>REST and GraphQL surface the host uses for GitHub (doc 21, Design). Never called by clients.</summary>
public interface IGitHubAdapter
{
    /// <summary>Searches by head branch first, then creates a draft PR. Returns the existing PR when one is open.</summary>
    Task<PullRequestRef> OpenDraftPrAsync(OpenPrRequest request, CancellationToken cancellationToken);

    Task<IReadOnlyList<PrFile>> GetPrFilesAsync(PrRef pr, string headSha, CancellationToken cancellationToken);

    Task PostReviewAsync(PrRef pr, ReviewRequest review, CancellationToken cancellationToken);

    /// <summary>Only called on a signed owner approval (doc 21, Merge request path).</summary>
    Task SquashMergeAsync(PrRef pr, MergeRequest merge, CancellationToken cancellationToken);
}

public sealed record PrRef(long InstallationId, string Owner, string Repo, int Number);

public sealed record PullRequestRef(PrRef Pr, string NodeId, string HeadSha, bool Draft);

public sealed record OpenPrRequest(long InstallationId, string Owner, string Repo, string HeadBranch, string BaseBranch, string Title, string Body);

public sealed record PrFile(string Path, string Status, int Additions, int Deletions, string? Patch);

public sealed record ReviewRequest(string HeadSha, string Event, string Body);

public sealed record MergeRequest(string HeadSha, string CommitTitle, string CommitMessage);
