using Bazaar.Contracts.Git;

namespace Bazaar.Client.GitGui;

/// <summary>
/// The seam to the server's git read routes (doc 52, Wire and routes). An implementation talks to the Bazaar server only;
/// there is no implementation that reaches a forge, and the client never holds a forge credential. Tests use
/// <see cref="InMemoryGitGuiClient"/>. A server-backed implementation lands with the wire route in a later change.
/// </summary>
public interface IGitGuiClient
{
    /// <summary>One page of PRs. <paramref name="page"/> is 1-based. <paramref name="state"/> is <c>open</c>, <c>closed</c>, <c>merged</c> or <c>all</c>.</summary>
    Task<GitPrPageDto> ListPrsAsync(string provider, string repo, string state, int page, int pageSize, CancellationToken cancellationToken = default);

    Task<GitPrDetailDto> GetPrAsync(string provider, string repo, long number, CancellationToken cancellationToken = default);

    /// <summary>The changed files at <paramref name="headSha"/>.</summary>
    /// <exception cref="HeadMovedException">The PR's head is no longer <paramref name="headSha"/>.</exception>
    Task<IReadOnlyList<GitPrFileDto>> GetFilesAsync(string provider, string repo, long number, string headSha, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<GitCheckDto>> GetChecksAsync(string provider, string repo, long number, CancellationToken cancellationToken = default);

    /// <summary>
    /// Sends the owner-signed squash merge to the server's neutral merge route. The server verifies the signature; the client never
    /// calls a forge. Returns the merged head sha.
    /// </summary>
    /// <exception cref="HeadMovedException">409: the PR head is no longer the one that was approved.</exception>
    /// <exception cref="GitMergeNotApprovedException">403: the server refused the approval, with no detail.</exception>
    /// <exception cref="GitRateLimitedException">429: the forge is rate limiting; retry later.</exception>
    /// <exception cref="GitServerUnavailableException">502 or another 5xx, or no connection.</exception>
    Task<GitMergedDto> MergeAsync(string provider, string repo, long number, GitMergeRequestDto request, CancellationToken cancellationToken = default);
}

/// <summary>The server answered <c>409 head_moved</c>: the head the client asked about is no longer current (doc 52).</summary>
public sealed class HeadMovedException : Exception
{
    public HeadMovedException(string currentSha)
        : base($"The head moved; the current head is {currentSha}.")
    {
        CurrentSha = currentSha;
    }

    /// <summary>The head the server now reports.</summary>
    public string CurrentSha { get; }
}
