namespace Bazaar.Server.GitHub.Auth;

/// <summary>
/// The token provider used until the operator points the module at the Identity Service
/// (<c>Bazaar:GitHub:IdentityService:BaseAddress</c>). It keeps the service graph resolvable and fails closed: every call throws,
/// so no GitHub request, and no merge, can be sent without a minted installation token (doc 21, Tokens).
/// </summary>
public sealed class UnconfiguredGitHubTokenProvider : IGitHubTokenProvider
{
    public Task<GitHubToken> GetTokenAsync(GitHubTokenScope scope, CancellationToken cancellationToken) =>
        throw new GitHubTokenException(
            "No GitHub token source is configured. Set Bazaar:GitHub:IdentityService:BaseAddress to the Identity Service endpoint.");
}
