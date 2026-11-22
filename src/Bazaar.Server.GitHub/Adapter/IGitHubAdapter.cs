namespace Bazaar.Server.GitHub.Adapter;

/// <summary>REST and GraphQL surface the host uses for GitHub (doc 21, Design). Never called by clients.</summary>
public interface IGitHubAdapter
{
    /// <summary>Searches by head branch first, then creates a draft PR. Returns the existing PR when one is open.</summary>
    Task<PullRequestRef> OpenDraftPrAsync(OpenPrRequest request, CancellationToken cancellationToken);

    Task<IReadOnlyList<PrFile>> GetPrFilesAsync(PrRef pr, string headSha, CancellationToken cancellationToken);

    Task PostReviewAsync(PrRef pr, ReviewRequest review, CancellationToken cancellationToken);

    /// <summary>The PR's current head sha. The owner's approval is void if this moves (doc 21, Merge request path).</summary>
    Task<string> GetHeadShaAsync(PrRef pr, CancellationToken cancellationToken);

    /// <summary>
    /// The state of every check on <paramref name="headSha"/>: check runs and legacy commit statuses together. The merge gate refuses
    /// anything but <see cref="PrChecksState.Passing"/> (doc 21, Merge request path: "checks pass").
    /// </summary>
    Task<PrChecksState> GetChecksStateAsync(PrRef pr, string headSha, CancellationToken cancellationToken);
}

/// <summary>Combined state of the checks on one commit.</summary>
public enum PrChecksState
{
    /// <summary>At least one check exists and every one finished green (success, neutral or skipped).</summary>
    Passing,

    /// <summary>At least one check failed, was cancelled, timed out or needs action.</summary>
    Failing,

    /// <summary>No check has failed, but at least one has not finished.</summary>
    Pending,

    /// <summary>The commit has no checks, so nothing can be verified. Treated as not passing.</summary>
    NoChecks,
}

public sealed record PrRef(long InstallationId, string Owner, string Repo, int Number);

public sealed record PullRequestRef(PrRef Pr, string NodeId, string HeadSha, bool Draft);

public sealed record OpenPrRequest(long InstallationId, string Owner, string Repo, string HeadBranch, string BaseBranch, string Title, string Body);

public sealed record PrFile(string Path, string Status, int Additions, int Deletions, string? Patch);

public sealed record ReviewRequest(string HeadSha, string Event, string Body);

public sealed record MergeRequest(string HeadSha, string CommitTitle, string CommitMessage);
