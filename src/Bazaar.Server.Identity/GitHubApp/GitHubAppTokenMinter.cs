namespace Bazaar.Server.Identity.GitHubApp;

/// <summary>
/// Returns the repositories an agent's lease allows for GitHub App tokens. The minter never widens beyond this list.
/// </summary>
public interface IGitHubLeaseScope
{
    /// <summary>Returns the bare repository names the agent's lease allows. An empty list allows nothing.</summary>
    ValueTask<IReadOnlyList<string>> GetAllowedRepositoriesAsync(string agentId, CancellationToken cancellationToken);
}

/// <summary>
/// Mints narrowed installation tokens host-side (doc 14, "GitHub Apps"). Every requested repository must be inside the
/// agent's lease scope, checked before any key is read. The returned <see cref="GitHubInstallationToken.ExpiresAt"/> is
/// the earlier of GitHub's expiry and the requested lifetime: the host must not use the token after it.
/// </summary>
public sealed class GitHubAppTokenMinter(
    IGitHubAppKeyStore keys,
    GitHubAppJwtSigner signer,
    IGitHubInstallationTokenClient client,
    IGitHubLeaseScope scope,
    TimeProvider time)
{
    private readonly IGitHubAppKeyStore _keys = keys ?? throw new ArgumentNullException(nameof(keys));
    private readonly GitHubAppJwtSigner _signer = signer ?? throw new ArgumentNullException(nameof(signer));
    private readonly IGitHubInstallationTokenClient _client = client ?? throw new ArgumentNullException(nameof(client));
    private readonly IGitHubLeaseScope _scope = scope ?? throw new ArgumentNullException(nameof(scope));
    private readonly TimeProvider _time = time ?? throw new ArgumentNullException(nameof(time));

    /// <summary>Mints a token narrowed to the request, for one agent.</summary>
    /// <param name="agentId">The agent whose App and lease apply.</param>
    /// <param name="request">The narrowed request. Its repositories must be inside the lease.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <exception cref="GitHubAppScopeException">A repository is outside the lease, or the lease has none.</exception>
    /// <exception cref="GitHubAppKeyNotFoundException">The agent has no App.</exception>
    public async ValueTask<GitHubInstallationToken> MintAsync(
        string agentId,
        GitHubAppTokenRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(agentId);
        ArgumentNullException.ThrowIfNull(request);

        var allowed = await _scope.GetAllowedRepositoriesAsync(agentId, cancellationToken).ConfigureAwait(false);
        if (allowed is null || allowed.Count == 0)
        {
            throw new GitHubAppScopeException($"Agent '{agentId}' has no repositories in its lease; nothing can be minted.");
        }

        foreach (var repository in request.Repositories)
        {
            if (!allowed.Contains(repository, StringComparer.OrdinalIgnoreCase))
            {
                throw new GitHubAppScopeException(
                    $"Repository '{repository}' is outside the lease for agent '{agentId}'. The token request is refused.");
            }
        }

        var credentials = await _keys.GetAppAsync(agentId, cancellationToken).ConfigureAwait(false);
        var jwt = _signer.CreateJwt(credentials.AppId, credentials.PrivateKeyPem);
        var token = await _client.CreateInstallationTokenAsync(request.InstallationId, jwt, request, cancellationToken)
            .ConfigureAwait(false);

        var usableUntil = _time.GetUtcNow().Add(request.Lifetime);
        var expiresAt = token.ExpiresAt < usableUntil ? token.ExpiresAt : usableUntil;
        return new GitHubInstallationToken(token.Token, expiresAt);
    }
}
