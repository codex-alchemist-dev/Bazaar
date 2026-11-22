using System.Text.Json;
using Avalonia.Headless.XUnit;
using Bazaar.Client.Abstractions.Server;
using Bazaar.Client.Community;
using Bazaar.Client.Community.Approvals;
using Bazaar.Client.Community.Avalonia;
using Bazaar.Client.Core.Panes;
using Bazaar.Client.Panes.Abstractions;
using Bazaar.Contracts.Panes;

namespace Bazaar.Client.Windows.Tests;

/// <summary>The Windows head composes the community panes (doc 36 work package 8, doc 40 section 4b).</summary>
public sealed class CommunityPanesTests
{
    private static PaneRegistry Compose() =>
        FirstPartyPanes.CreateRegistry(
            CommunityPanes.Modules(),
            features: new PaneModuleContext().WithCommunity(ServerEndpoint.Development));

    [Fact]
    public void Modules_are_the_drafts_and_log_panes()
    {
        Assert.Equal([PaneKinds.CommunityDrafts, PaneKinds.CommunityLog], CommunityPanes.Modules().Select(module => module.Id));
    }

    [Fact]
    public void Context_carries_the_http_decision_sender_and_no_signer()
    {
        var context = new PaneModuleContext().WithCommunity(ServerEndpoint.Development);

        Assert.IsType<HttpDraftReviewDecisionSender>(context.Get<IDraftReviewDecisionSender>());
        Assert.Null(context.Get<IDecisionSigner>());
    }

    [AvaloniaFact]
    public void Registry_resolves_both_community_kinds_to_views()
    {
        var registry = Compose();

        Assert.Contains(PaneKinds.CommunityDrafts, registry.Kinds);
        Assert.Contains(PaneKinds.CommunityLog, registry.Kinds);

        var drafts = registry.Create(new PaneDescriptor(PaneKinds.CommunityDrafts, "Drafts", string.Empty, JsonSerializer.SerializeToElement(new { cards = Array.Empty<object>() })));
        Assert.IsType<DraftReviewPaneViewModel>(drafts);
        Assert.NotNull(registry.CreateView(drafts));

        var log = registry.Create(new PaneDescriptor(PaneKinds.CommunityLog, "Community log", string.Empty, JsonSerializer.SerializeToElement(new { })));
        Assert.IsType<CommunityLogPaneViewModel>(log);
        Assert.NotNull(registry.CreateView(log));
    }
}
