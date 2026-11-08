using System.Collections.Concurrent;

namespace Bazaar.Server.Identity.GitHubApp;

/// <summary>In-memory <see cref="IGitHubAppKeyStore"/> for tests and local fakes. Holds keys in process memory only.</summary>
public sealed class InMemoryGitHubAppKeyStore : IGitHubAppKeyStore
{
    private readonly ConcurrentDictionary<string, GitHubAppCredentials> _apps = new(StringComparer.Ordinal);

    /// <summary>Registers or replaces the App for an agent.</summary>
    /// <param name="agentId">The agent that owns the App.</param>
    /// <param name="credentials">The App id and PEM key.</param>
    public void Set(string agentId, GitHubAppCredentials credentials)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(agentId);
        ArgumentNullException.ThrowIfNull(credentials);
        _apps[agentId] = credentials;
    }

    /// <inheritdoc />
    public ValueTask<GitHubAppCredentials> GetAppAsync(string agentId, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (agentId is null || !_apps.TryGetValue(agentId, out var credentials))
        {
            throw new GitHubAppKeyNotFoundException(agentId ?? string.Empty);
        }

        return ValueTask.FromResult(credentials);
    }
}
