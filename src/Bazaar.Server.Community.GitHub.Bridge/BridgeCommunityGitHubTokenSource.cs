using Bazaar.GitHub.Auth;

namespace Bazaar.Server.Community.GitHub.Bridge;

/// <summary>Binds <see cref="ICommunityGitHubTokenSource"/> to Bazaar.GitHub's <see cref="IGitHubTokenProvider"/> (doc 21, Tokens).</summary>
public sealed class BridgeCommunityGitHubTokenSource(IGitHubTokenProvider tokens, ICommunityInstallationResolver installations) : ICommunityGitHubTokenSource
{
    // The adapter files issues, comments and draft PRs; nothing else.
    private static readonly IReadOnlyDictionary<string, string> _permissions =
        new Dictionary<string, string> { ["issues"] = "write", ["pull_requests"] = "write" };

    public async Task<string> GetInstallationTokenAsync(string owner, string repo, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrEmpty(owner);
        ArgumentException.ThrowIfNullOrEmpty(repo);
        try
        {
            var installation = await installations.ResolveAsync(owner, repo, cancellationToken).ConfigureAwait(false)
                ?? throw BridgeFailure.Refusal("The GitHub App is not installed on that repository.");
            var token = await tokens.GetTokenAsync(
                new GitHubTokenScope(installation.InstallationId, [installation.RepositoryId], _permissions), cancellationToken).ConfigureAwait(false);
            return token.Value;
        }
        catch (Exception ex) when (ex is not (OperationCanceledException or CommunityPostException))
        {
            throw BridgeFailure.From(ex, "Getting an installation token");
        }
    }
}
