namespace Bazaar.Contracts.Git;

/// <summary>
/// The body of <c>POST /github/merge</c> (doc 21, Merge route): one PR, one merge request and the owner's signed approval of
/// exactly that merge. The client signs what its card showed; the server checks it before anything reaches GitHub.
/// </summary>
/// <param name="Pr">The pull request to merge.</param>
/// <param name="Merge">The squash merge, pinned to one head sha.</param>
/// <param name="Approval">The owner's signed approval (doc 32 section 5a).</param>
public sealed record GitHubMergeRequestDto(GitHubPrDto Pr, GitHubMergeDto Merge, OwnerApprovalDto Approval);

/// <summary>A pull request by owner, repository and number. The server picks the installation, so a client cannot.</summary>
public sealed record GitHubPrDto(string Owner, string Repo, int Number);

/// <summary>A squash merge pinned to <paramref name="HeadSha"/>. The merge is void if the head moved.</summary>
public sealed record GitHubMergeDto(string HeadSha, string CommitTitle, string CommitMessage);

/// <summary>
/// The signed fields of the owner payload (doc 32 section 5a) and the 64-byte signature. Binary values are base64 on the wire.
/// Times are Unix milliseconds, the form the signed text uses.
/// </summary>
public sealed record OwnerApprovalDto(
    string Action,
    Guid ApprovalId,
    byte[] ArgsHash,
    string? HeadSha,
    Guid HumanId,
    string DeviceId,
    long IssuedAtMs,
    long ExpiresAtMs,
    byte[] Nonce,
    byte[] Signature);

/// <summary>A successful merge.</summary>
public sealed record GitHubMergeResultDto(bool Merged);

/// <summary>A refusal. <paramref name="Reason"/> is short text for the owner and is null for codes that need none.</summary>
public sealed record GitHubMergeErrorDto(string Error, string? Reason = null);
