namespace Bazaar.Server.Git.GitLab;

/// <summary>
/// Settings for the GitLab REST v4 adapter (doc 52). The default is gitlab.com. A self-managed instance sets its own root.
/// </summary>
public sealed class GitLabOptions
{
    /// <summary>The instance root, for example <c>https://gitlab.com</c>. Must be https, because the token travels in each request.</summary>
    public Uri BaseUrl { get; set; } = new("https://gitlab.com/");

    /// <summary>
    /// Retries after the first attempt. 429 is retried on every method. 5xx is retried on GET only: a POST or PUT that reached
    /// GitLab may already have written, so it is never replayed on a server error.
    /// </summary>
    public int MaxRetries { get; set; } = 3;

    /// <summary>The longest wait honoured from a <c>Retry-After</c> header. Longer values are cut to this.</summary>
    public TimeSpan MaxRetryAfter { get; set; } = TimeSpan.FromSeconds(60);
}
