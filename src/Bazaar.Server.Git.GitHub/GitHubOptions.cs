namespace Bazaar.Server.Git.GitHub;

/// <summary>
/// Settings for the GitHub REST adapter (doc 52). The default is github.com. GitHub Enterprise Server sets its own API root.
/// </summary>
public sealed class GitHubOptions
{
    /// <summary>
    /// The REST API root, for example <c>https://api.github.com</c> or, on GitHub Enterprise Server, <c>https://ghe.example.com/api/v3</c>.
    /// Must be https, because the token travels in each request.
    /// </summary>
    public Uri BaseUrl { get; set; } = new("https://api.github.com/");

    /// <summary>
    /// Retries after the first attempt. Rate-limit responses (429, or 403 with <c>Retry-After</c> or an exhausted quota) are retried
    /// on every method. 5xx is retried on GET only: a POST or PUT that reached GitHub may already have written, so it is never
    /// replayed on a server error.
    /// </summary>
    public int MaxRetries { get; set; } = 3;

    /// <summary>The longest wait honoured from <c>Retry-After</c> or the quota reset time. A quota reset further away is not waited for.</summary>
    public TimeSpan MaxRetryAfter { get; set; } = TimeSpan.FromSeconds(60);
}
