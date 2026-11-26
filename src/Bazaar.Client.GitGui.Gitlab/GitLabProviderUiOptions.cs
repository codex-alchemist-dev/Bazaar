namespace Bazaar.Client.GitGui.Gitlab;

/// <summary>
/// Options for the GitLab provider UI. The default targets gitlab.com; a self-managed instance sets its own https origin
/// (doc 52, Client). The base URL is validated when the UI is built, so a bad value fails at setup, not on a link.
/// </summary>
public sealed record GitLabProviderUiOptions
{
    /// <summary>The https origin of the GitLab instance, optionally with a path prefix. No userinfo, query or fragment.</summary>
    public string BaseUrl { get; init; } = "https://gitlab.com";
}
