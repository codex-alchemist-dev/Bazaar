using System.Collections.Concurrent;
using System.Text;

namespace Bazaar.Server.GitHub.Auth;

/// <summary>
/// Supplies GitHub installation tokens minted by the Identity Service (doc 21, Tokens; doc 14).
/// Tokens are cached in memory until five minutes before their reported expiry. Concurrent callers for the same
/// scope share one exchange. Failed exchanges are not cached, so the next call tries again.
/// </summary>
public sealed class IdentityServiceGitHubTokenProvider : IGitHubTokenProvider
{
    /// <summary>How long before the reported expiry a cached token is treated as stale.</summary>
    public static readonly TimeSpan RefreshMargin = TimeSpan.FromMinutes(5);

    private readonly IIdentityServiceTokenClient _client;
    private readonly TimeProvider _timeProvider;
    private readonly ConcurrentDictionary<ScopeKey, GitHubToken> _cache = new();
    private readonly ConcurrentDictionary<ScopeKey, SemaphoreSlim> _gates = new();

    public IdentityServiceGitHubTokenProvider(IIdentityServiceTokenClient client, TimeProvider? timeProvider = null)
    {
        _client = client ?? throw new ArgumentNullException(nameof(client));
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public async Task<GitHubToken> GetTokenAsync(GitHubTokenScope scope, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(scope);
        var key = ScopeKey.From(scope);

        if (TryGetFresh(key, out var cached))
        {
            return cached;
        }

        // One gate per scope: callers that miss the cache wait here, then re-check it before exchanging.
        var gate = _gates.GetOrAdd(key, static _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (TryGetFresh(key, out cached))
            {
                return cached;
            }

            var token = await ExchangeAsync(scope, cancellationToken).ConfigureAwait(false);
            _cache[key] = token;
            return token;
        }
        finally
        {
            gate.Release();
        }
    }

    private bool TryGetFresh(ScopeKey key, out GitHubToken token)
    {
        if (_cache.TryGetValue(key, out var candidate) && _timeProvider.GetUtcNow() < candidate.ExpiresAt - RefreshMargin)
        {
            token = candidate;
            return true;
        }

        token = null!;
        return false;
    }

    private async Task<GitHubToken> ExchangeAsync(GitHubTokenScope scope, CancellationToken cancellationToken)
    {
        IdentityTokenResponse response = await _client.ExchangeAsync(scope, cancellationToken).ConfigureAwait(false);
        string? errorCode = SanitizeErrorCode(response.ErrorCode);

        if (response.StatusCode is < 200 or > 299)
        {
            throw new GitHubTokenException(
                $"GitHub installation token exchange failed (HTTP {response.StatusCode}, error code {errorCode ?? "none"}).",
                response.StatusCode,
                errorCode);
        }

        if (string.IsNullOrEmpty(response.Token) || response.ExpiresAt is not { } expiresAt)
        {
            throw new GitHubTokenException(
                "GitHub installation token exchange returned a response without a token or expiry.",
                response.StatusCode,
                errorCode);
        }

        if (expiresAt <= _timeProvider.GetUtcNow())
        {
            throw new GitHubTokenException(
                "GitHub installation token exchange returned a token that has already expired.",
                response.StatusCode,
                errorCode);
        }

        return new GitHubToken(response.Token, expiresAt);
    }

    /// <summary>
    /// Maps an Identity Service error code to a value safe for exception messages: letters, digits, '_', '.' and '-',
    /// at most 64 characters. Anything else becomes "unknown". Null stays null.
    /// </summary>
    internal static string? SanitizeErrorCode(string? code)
    {
        if (code is null)
        {
            return null;
        }

        if (code.Length == 0 || !code.All(c => char.IsAsciiLetterOrDigit(c) || c is '_' or '.' or '-'))
        {
            return "unknown";
        }

        return code.Length <= _maxErrorCodeLength ? code : code[.._maxErrorCodeLength];
    }

    private const int _maxErrorCodeLength = 64;

    /// <summary>
    /// Cache and lock key: the installation, repositories and permissions one token is narrowed to.
    /// Each component is length-prefixed, so no value can imitate a separator and collide with another scope.
    /// </summary>
    private readonly record struct ScopeKey(long InstallationId, string RepositoryIds, string Permissions)
    {
        public static ScopeKey From(GitHubTokenScope scope)
        {
            var permissions = new StringBuilder();
            foreach (var (name, level) in scope.Permissions.OrderBy(p => p.Key, StringComparer.Ordinal))
            {
                permissions.Append(Part(name)).Append(Part(level));
            }

            return new(scope.InstallationId, Part(string.Join(",", scope.RepositoryIds.Order())), permissions.ToString());
        }

        private static string Part(string value) => $"{value.Length}:{value}";
    }
}
