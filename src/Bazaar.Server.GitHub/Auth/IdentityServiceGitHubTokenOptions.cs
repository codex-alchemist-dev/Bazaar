namespace Bazaar.Server.GitHub.Auth;

/// <summary>Settings for the Identity Service GitHub token exchange.</summary>
public sealed class IdentityServiceGitHubTokenOptions
{
    /// <summary>Path of the exchange endpoint, resolved against the injected <c>HttpClient.BaseAddress</c>.</summary>
    // TODO(doc 14): confirm endpoint
    public string ExchangePath { get; init; } = "/v1/github/installation-tokens";
}
