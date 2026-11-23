using System.Collections.Concurrent;
using Bazaar.Server.CodingGit;

namespace Bazaar.Server.Identity.GitHubApp;

/// <summary>The agent's human-readable name, used as the commit author name. The host binds it to its roster.</summary>
public interface IGitHubAuthorNames
{
    /// <summary>The display name, or null when the agent is unknown.</summary>
    ValueTask<string?> FindAsync(string agentId, CancellationToken cancellationToken);
}

/// <summary>
/// <see cref="IAgentGitIdentity"/> from the agent's own GitHub App (doc 14, "GitHub Apps"). Author name is the display name;
/// author email is the App's bot noreply address, looked up once per agent and cached. Any gap throws so the commit fails closed.
/// </summary>
public sealed class GitHubAppAgentGitIdentity : IAgentGitIdentity
{
    private readonly IGitHubAppKeyStore _keys;
    private readonly GitHubAppJwtSigner _signer;
    private readonly IGitHubAppBotClient _bots;
    private readonly IGitHubAuthorNames _names;
    private readonly ConcurrentDictionary<string, GitHubAppBot> _cache = new(StringComparer.Ordinal);

    /// <summary>Creates the identity source.</summary>
    public GitHubAppAgentGitIdentity(IGitHubAppKeyStore keys, GitHubAppJwtSigner signer, IGitHubAppBotClient bots, IGitHubAuthorNames names)
    {
        ArgumentNullException.ThrowIfNull(keys);
        ArgumentNullException.ThrowIfNull(signer);
        ArgumentNullException.ThrowIfNull(bots);
        ArgumentNullException.ThrowIfNull(names);
        _keys = keys;
        _signer = signer;
        _bots = bots;
        _names = names;
    }

    /// <inheritdoc />
    public async ValueTask<GitAuthor> GetAuthorAsync(string agentId, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(agentId);

        var name = await _names.FindAsync(agentId, cancellationToken).ConfigureAwait(false);
        if (name is null)
        {
            throw new InvalidOperationException($"No display name is known for agent '{agentId}', so no commit author can be built.");
        }

        name = name.Trim();
        if (name.Length == 0 || name.Any(c => char.IsControl(c) || c is '<' or '>'))
        {
            throw new InvalidOperationException($"The display name of agent '{agentId}' is empty or has control characters or angle brackets.");
        }

        if (!_cache.TryGetValue(agentId, out var bot))
        {
            var app = await _keys.GetAppAsync(agentId, cancellationToken).ConfigureAwait(false);
            var jwt = _signer.CreateJwt(app.AppId, app.PrivateKeyPem);
            bot = await _bots.GetBotAsync(jwt, cancellationToken).ConfigureAwait(false);
            _cache[agentId] = bot;
        }

        return new GitAuthor(name, bot.NoreplyEmail);
    }
}
