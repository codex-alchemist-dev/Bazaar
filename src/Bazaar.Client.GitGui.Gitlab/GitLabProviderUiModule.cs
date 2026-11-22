namespace Bazaar.Client.GitGui.Gitlab;

/// <summary>The GitLab provider client module: the provider UI a head composes into the git UI registry (doc 52, Client).</summary>
public static class GitLabProviderUiModule
{
    /// <summary>The gitlab.com provider UI.</summary>
    public static GitLabProviderUi Instance { get; } = new();

    /// <summary>The gitlab.com provider UI, in registration order. Self-managed hosts use <see cref="ProviderUisFor"/>.</summary>
    public static IReadOnlyList<IGitProviderUi> ProviderUis { get; } = [Instance];

    /// <summary>The provider UI for a self-managed instance, validated from <paramref name="options"/>.</summary>
    /// <exception cref="ArgumentException">The base URL is not an acceptable https origin.</exception>
    public static IReadOnlyList<IGitProviderUi> ProviderUisFor(GitLabProviderUiOptions options) =>
        [new GitLabProviderUi(options)];
}
