namespace Bazaar.Server.GitHub.Auth;

/// <summary>
/// Exchanges a narrowed scope for an installation token at the Identity Service (doc 14).
/// Implementations report non-success responses as data; <see cref="IdentityServiceGitHubTokenProvider"/> decides what they mean.
/// </summary>
public interface IIdentityServiceTokenClient
{
    Task<IdentityTokenResponse> ExchangeAsync(GitHubTokenScope scope, CancellationToken cancellationToken);
}

/// <summary>
/// Raw result of one exchange. <see cref="Token"/> is secret; <see cref="ToString"/> never prints it.
/// </summary>
public sealed record IdentityTokenResponse(int StatusCode, string? Token, DateTimeOffset? ExpiresAt, string? ErrorCode)
{
    public override string ToString() =>
        $"IdentityTokenResponse(HTTP {StatusCode}, expires {ExpiresAt:O}, error code {ErrorCode ?? "none"}, token redacted)";
}
