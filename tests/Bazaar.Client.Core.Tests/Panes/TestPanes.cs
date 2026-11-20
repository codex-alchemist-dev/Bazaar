using Bazaar.Client.Abstractions.Panes;
using Bazaar.Client.Chat;
using Bazaar.Client.Chat.Abstractions;
using Bazaar.Client.Constellation;
using Bazaar.Client.Constellation.Abstractions;
using Bazaar.Client.Core.Panes;
using Bazaar.Client.Dashboard;
using Bazaar.Client.Engines;
using Bazaar.Client.Gc;
using Bazaar.Client.Panes.Abstractions;
using Bazaar.Client.RyuseiFuda;
using Bazaar.Client.Vault;

namespace Bazaar.Client.Core.Tests.Panes;

/// <summary>The pane modules a full head ships, composed the way the Windows and Android heads compose them.</summary>
internal static class TestPanes
{
    public static IReadOnlyList<IPaneModule> Modules { get; } =
    [
        RyuseiFudaPanes.Board,
        new DashboardPaneModule(),
        new ConstellationPaneModule(),
        new GcPaneModule(),
        new VaultSearchPaneModule(),
        .. RyuseiFudaPanes.Modules,
        new EnginePaneModule(),
    ];

    public static PaneRegistry CreateRegistry(
        Action<string>? report = null,
        IConstellationTileSource? tiles = null,
        IChatViewModel? chat = null,
        VaultApi? vault = null) =>
        FirstPartyPanes.CreateRegistry(Modules, report, Features(tiles, chat, vault));

    private static PaneModuleContext? Features(IConstellationTileSource? tiles, IChatViewModel? chat, VaultApi? vault)
    {
        if (tiles is null && chat is null && vault is null) return null;
        var context = new PaneModuleContext();
        if (tiles is not null) context = context.With<IConstellationTileSource>(tiles);
        if (chat is not null)
        {
            context = context.With<IChatViewModel>(chat).With<IChatViewFactory>(ChatViewFactory.Instance);
        }
        if (vault is not null) context = context.With<VaultApi>(vault);
        return context;
    }
}
