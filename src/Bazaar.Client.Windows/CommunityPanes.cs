using Bazaar.Client.Abstractions.Server;
using Bazaar.Client.Community;
using Bazaar.Client.Community.Approvals;
using Bazaar.Client.Community.Avalonia;
using Bazaar.Client.Panes.Abstractions;

namespace Bazaar.Client.Windows;

/// <summary>
/// Composition of the community panes for this head (doc 36 work package 8, doc 40 section 4b). The head only wires seams:
/// the pane modules, and the decision sender and the log source over the head's endpoint. [PROPOSED, 2026-11-22]
/// </summary>
/// <remarks>
/// No <see cref="IDecisionSigner"/> is supplied: the client holds no enrolled device key store yet, so the drafts pane keeps
/// its built-in "No device signing key is available" refusal and sends nothing. That gap closes when the key store lands.
/// The log pane reads <c>GET /community/log</c> through <see cref="HttpCommunityLogSource"/>.
/// </remarks>
public static class CommunityPanes
{
    /// <summary>The community pane modules, in registry order.</summary>
    public static IReadOnlyList<IPaneModule> Modules() => [new CommunityDraftsPaneModule(), new CommunityLogPaneModule()];

    /// <summary>Adds the draft decision sender bound to <paramref name="endpoint"/> to the feature context.</summary>
    public static PaneModuleContext WithCommunity(this PaneModuleContext context, ServerEndpoint endpoint)
    {
        ArgumentNullException.ThrowIfNull(context);
        return context
            .With<IDraftReviewDecisionSender>(HttpDraftReviewDecisionSender.Create(endpoint))
            .With<ICommunityLogSource>(HttpCommunityLogSource.Create(endpoint));
    }
}
