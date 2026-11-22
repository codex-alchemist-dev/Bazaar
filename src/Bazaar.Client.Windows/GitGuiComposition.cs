using Bazaar.Client.Abstractions.Server;
using Bazaar.Client.GitGui;
using Bazaar.Client.GitGui.Diff;
using Bazaar.Client.GitGui.Github;
using Bazaar.Client.GitGui.Gitlab;
using Bazaar.Client.GitGui.Merge;
using Bazaar.Client.Panes.Abstractions;
using Bazaar.Client.SignIn;

namespace Bazaar.Client.Windows;

/// <summary>Hands the Git client module (doc 52, Client) what its panes read, through <see cref="PaneModuleContext.With{T}"/>.</summary>
public static class GitGuiComposition
{
    /// <summary>
    /// Injects <see cref="IGitGuiClient"/> (over the company server), <see cref="IGitProviderUiRegistry"/> (GitHub and GitLab),
    /// <see cref="IGitGuiSettings"/> and <see cref="DiffSurfaceRegistry"/>.
    /// </summary>
    /// <remarks>
    /// Left out on purpose: <c>IOwnerMergeApprover</c>, because no desktop device-signer exists yet; the merge card then shows that
    /// this client cannot approve merges. <c>IOwnerMergeIdentity</c> is added separately by <see cref="WithOwnerMergeIdentity"/>.
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

    /// <summary>
    /// Injects <see cref="IOwnerMergeIdentity"/> for the signed-in person, and nothing when no one is signed in or the principal
    /// has no name and email to sign off with (the merge card then says it cannot approve).
    /// </summary>
    /// <param name="context">The pane context.</param>
    /// <param name="principal">The signed-in principal, read from the session by the caller; null when signed out.</param>
    /// <param name="profile">The per-human sign-off field; null means every field is unset.</param>
    public static PaneModuleContext WithOwnerMergeIdentity(this PaneModuleContext context, MergePrincipal? principal, IMergeProfileStore? profile = null)
    {
        ArgumentNullException.ThrowIfNull(context);
        var identity = SessionOwnerMergeIdentity.TryCreate(principal, profile);
        return identity is null ? context : context.With<IOwnerMergeIdentity>(identity);
    }

    /// <summary>
    /// Reads the saved session and, when there is one, resolves its principal with <paramref name="resolvePrincipal"/>, then calls
    /// <see cref="WithOwnerMergeIdentity(PaneModuleContext, MergePrincipal?, IMergeProfileStore?)"/>. No session means no identity.
    /// </summary>
    public static async Task<PaneModuleContext> WithOwnerMergeIdentityAsync(
        this PaneModuleContext context,
        ISessionStore sessions,
        Func<ClientSession, CancellationToken, Task<MergePrincipal?>> resolvePrincipal,
        IMergeProfileStore? profile = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(sessions);
        ArgumentNullException.ThrowIfNull(resolvePrincipal);
        var session = await sessions.LoadAsync(cancellationToken).ConfigureAwait(false);
        var principal = session is null ? null : await resolvePrincipal(session, cancellationToken).ConfigureAwait(false);
        return context.WithOwnerMergeIdentity(principal, profile);
    }
}
