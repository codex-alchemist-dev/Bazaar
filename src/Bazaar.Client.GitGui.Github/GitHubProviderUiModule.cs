namespace Bazaar.Client.GitGui.Github;

/// <summary>The GitHub provider client module: the provider UI a head composes into the git UI registry (doc 52, Client).</summary>
public static class GitHubProviderUiModule
{
    /// <summary>The GitHub provider UI.</summary>
    public static GitHubProviderUi Instance { get; } = new();

    /// <summary>The provider UIs this module contributes, in registration order.</summary>
    public static IReadOnlyList<IGitProviderUi> ProviderUis { get; } = [Instance];
}
