using Bazaar.Client.Abstractions.Server;
using Bazaar.Client.GitGui;
using Bazaar.Client.GitGui.Diff;
using Bazaar.Client.GitGui.Github;
using Bazaar.Client.GitGui.Gitlab;
using Bazaar.Client.Panes.Abstractions;

namespace Bazaar.Client.Windows;

/// <summary>Hands the Git client module (doc 52, Client) what its panes read, through <see cref="PaneModuleContext.With{T}"/>.</summary>
public static class GitGuiComposition
{
    /// <summary>
    /// Injects <see cref="IGitGuiClient"/> (over the company server), <see cref="IGitProviderUiRegistry"/> (GitHub and GitLab),
    /// <see cref="IGitGuiSettings"/> and <see cref="DiffSurfaceRegistry"/>.
    /// </summary>
    /// <remarks>
    /// Left out on purpose: <c>IOwnerMergeApprover</c> and <c>IOwnerMergeIdentity</c>, because no desktop device-signer exists yet and
    /// the design does not say where the human id comes from; the merge card then shows that this client cannot approve merges.
    /// <see cref="IGitRepoSource"/> is also left out: no implementation or server route exists, so the Git view says so.
    /// </remarks>
    public static PaneModuleContext WithGitGui(this PaneModuleContext context, ServerEndpoint endpoint)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(endpoint);
        return context
            .With<IGitGuiClient>(new ServerGitGuiClient(new HttpGitTransport(endpoint.CreateHttpClient(), () => endpoint.Credential)))
            .With<IGitProviderUiRegistry>(new GitProviderUiRegistry([.. GitHubProviderUiModule.ProviderUis, .. GitLabProviderUiModule.ProviderUis]))
            .With<IGitGuiSettings>(GitGuiSettings.Default)
            .With<DiffSurfaceRegistry>(new DiffSurfaceRegistry());
    }
}
