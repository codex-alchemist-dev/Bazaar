namespace Bazaar.Server.Identity.GitHubApp;

/// <summary>
/// A short-lived push credential for the host-side git provider only. It exposes no path, file or serialized form.
/// <see cref="ToString"/> redacts the token, so logs and diagnostics never carry it.
/// </summary>
public sealed class HostPushCredential
{
    internal HostPushCredential(string repository, string token, DateTimeOffset expiresAt)
    {
        Repository = repository;
        Token = token;
        ExpiresAt = expiresAt;
    }

    /// <summary>The bare repository name the credential pushes to.</summary>
    public string Repository { get; }

    /// <summary>The access token. The host git provider reads it in memory and must not write it to disk or a VM.</summary>
    public string Token { get; }

    /// <summary>The usable-until time. The host must stop using the credential after it.</summary>
    public DateTimeOffset ExpiresAt { get; }

    /// <summary>Returns the repository and expiry with the token redacted.</summary>
    public override string ToString() =>
        $"HostPushCredential(Repository={Repository}, ExpiresAt={ExpiresAt:O}, Token=[redacted])";
}

/// <summary>
/// Hands the host git provider a push token scoped to one repository (doc 14: git pushes run host-side and the token
/// never enters a VM). The provider has no path or file output; the caller decides how to use the token in memory.
/// </summary>
public sealed class HostPushTokenProvider(GitHubAppTokenMinter minter)
{
    /// <summary>The usable window for a push token. Short, so a leaked token expires quickly.</summary>
    public static readonly TimeSpan PushLifetime = TimeSpan.FromMinutes(10);

    private static readonly IReadOnlyDictionary<string, string> _pushPermissions =
        new Dictionary<string, string>(StringComparer.Ordinal) { ["contents"] = "write" };

    private readonly GitHubAppTokenMinter _minter = minter ?? throw new ArgumentNullException(nameof(minter));

    /// <summary>Mints a push credential for one repository.</summary>
    /// <param name="agentId">The agent whose App and lease apply.</param>
    /// <param name="installationId">The App installation that holds the repository.</param>
    /// <param name="repository">The bare repository name, without the owner.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    public async ValueTask<HostPushCredential> GetPushCredentialAsync(
        string agentId,
        long installationId,
        string repository,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(agentId);
        ArgumentException.ThrowIfNullOrWhiteSpace(repository);

        var request = new GitHubAppTokenRequest(
            installationId,
            [repository],
            _pushPermissions,
            PushLifetime);
        var token = await _minter.MintAsync(agentId, request, cancellationToken).ConfigureAwait(false);
        return new HostPushCredential(repository, token.Token, token.ExpiresAt);
    }
}
