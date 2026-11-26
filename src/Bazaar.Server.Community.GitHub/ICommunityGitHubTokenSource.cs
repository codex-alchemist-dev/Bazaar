namespace Bazaar.Server.Community.GitHub;

/// <summary>
/// Supplies a short-lived GitHub App installation token for one repository (docs/design/21, doc 14). This module does not reference
/// the GitHub module: the host bridges this seam to Bazaar.Server.GitHub's <c>IGitHubTokenProvider</c> (Identity Service). The token is never logged or put in an exception message.
/// </summary>
public interface ICommunityGitHubTokenSource
{
    Task<string> GetInstallationTokenAsync(string owner, string repo, CancellationToken cancellationToken);
}
