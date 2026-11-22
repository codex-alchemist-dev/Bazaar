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

/// <summary>
/// The body of <c>POST /git/{provider}/repos/{owner}/{repo}/prs/{n}/merge</c> (doc 52, Merge gate). The merge fields are what the
/// owner signed over (through the args hash); <paramref name="Approval"/> is the owner's signed approval, which the server verifies.
/// </summary>
public sealed record GitMergeRequestDto(string HeadSha, string CommitTitle, string SignOff, GitOwnerApprovalDto Approval);

/// <summary>
/// An owner-signed approval on the wire (doc 32 section 5a): the payload fields plus the device signature. Byte fields are base64.
/// The server never echoes the signature back.
/// </summary>
public sealed record GitOwnerApprovalDto(
    string Action,
    Guid ApprovalId,
    string ArgsHashBase64,
    string? HeadSha,
    Guid HumanId,
    string DeviceId,
    DateTimeOffset IssuedAt,
    DateTimeOffset ExpiresAt,
    string NonceBase64,
    string SignatureBase64);

/// <summary>The body of a <c>200</c> merge answer: the head sha that was merged.</summary>
public sealed record GitMergedDto(string HeadSha);

/// <summary>The body of a <c>429 rate_limited</c> answer. <paramref name="RetryAfterSeconds"/> is null when the forge gave no hint.</summary>
public sealed record GitRateLimitedDto(string Error, int? RetryAfterSeconds);

/// <summary>
/// One repository the Git client can show (<c>GET /git/repos</c>). Provider and full name only, plus the default branch when the
/// server knows it. It never carries the host clone path, an installation id or any credential.
/// </summary>
public sealed record GitRepoDto(string Provider, string FullName, string? DefaultBranch);
