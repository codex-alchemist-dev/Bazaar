namespace Bazaar.Contracts.Git;

/// <summary>
/// Wire shapes for the server-proxied git reads (doc 52, Wire and routes). Clients never call a forge; they read these from
/// <c>GET /git/{provider}/repos/{owner}/{repo}/prs</c> and its sub-routes. States and statuses are lower-case strings so a newer
/// server can add values without breaking an older client.
/// </summary>
public sealed record GitPrSummaryDto(
    string Provider,
    string Repo,
    long Number,
    string Title,
    string AuthorLogin,
    string State,
    bool Draft,
    string HeadSha,
    string HeadBranch,
    string BaseBranch,
    long UpdatedAtMs);

/// <summary>A page of PRs. <paramref name="Stale"/> is true when the forge was unreachable and the list is the last cached one.</summary>
public sealed record GitPrPageDto(IReadOnlyList<GitPrSummaryDto> Items, bool HasMore, bool Stale);

public sealed record GitReviewDto(string AuthorLogin, string Verdict, string HeadSha, long SubmittedAtMs);

public sealed record GitPrDetailDto(
    GitPrSummaryDto Summary,
    string Body,
    int ChangedFiles,
    int Additions,
    int Deletions,
    IReadOnlyList<GitReviewDto> Reviews);

/// <summary>One changed file. <paramref name="Truncated"/> is true when the patch was cut at <c>git.max_diff_bytes</c>.</summary>
public sealed record GitPrFileDto(string Path, string Status, int Additions, int Deletions, string? Patch, bool Truncated);

public sealed record GitCheckDto(string Name, string State, string HeadSha, string? DetailsUrl);

/// <summary>The body of a <c>409 head_moved</c> answer: the head the client asked about is no longer current.</summary>
public sealed record GitHeadMovedDto(string CurrentHeadSha);
