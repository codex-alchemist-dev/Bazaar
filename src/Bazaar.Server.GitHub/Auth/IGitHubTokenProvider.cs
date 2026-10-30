namespace Bazaar.Server.GitHub.Auth;

/// <summary>
/// Supplies a short-lived GitHub App installation token narrowed to one lease (doc 21, Tokens).
/// Tokens stay in memory, are never logged, and never leave the host. Implementations live behind the Identity Service (doc 14).
/// </summary>
public interface IGitHubTokenProvider
{
    /// <summary>Returns a token for the installation and repositories named by <paramref name="scope"/>.</summary>
    Task<GitHubToken> GetTokenAsync(GitHubTokenScope scope, CancellationToken cancellationToken);
}

/// <summary>Repositories and permissions one token is narrowed to.</summary>
public sealed record GitHubTokenScope(long InstallationId, IReadOnlyList<long> RepositoryIds, IReadOnlyDictionary<string, string> Permissions);

/// <summary>An installation token. <see cref="ToString"/> never prints the value.</summary>
public sealed record GitHubToken(string Value, DateTimeOffset ExpiresAt)
{
    public override string ToString() => $"GitHubToken(expires {ExpiresAt:O}, value redacted)";
}
