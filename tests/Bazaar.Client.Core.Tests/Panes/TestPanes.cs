using Bazaar.Client.Abstractions.Panes;
using Bazaar.Client.Constellation;
using Bazaar.Client.Constellation.Abstractions;
using Bazaar.Client.Core.Chat;
using Bazaar.Client.Core.Panes;
using Bazaar.Client.Dashboard;
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
        .. FirstPartyPanes.Modules,
        new VaultSearchPaneModule(),
        .. RyuseiFudaPanes.Modules,
    ];

    public static PaneRegistry CreateRegistry(
        Action<string>? report = null,
        IConstellationTileSource? tiles = null,
        ChatViewModel? chat = null,
        VaultApi? vault = null) =>
        FirstPartyPanes.CreateRegistry(Modules, report, chat, Features(tiles, vault));

    private static PaneModuleContext? Features(IConstellationTileSource? tiles, VaultApi? vault)
    {
        if (tiles is null && vault is null) return null;
        var context = new PaneModuleContext();
        if (tiles is not null) context = context.With<IConstellationTileSource>(tiles);
        if (vault is not null) context = context.With<VaultApi>(vault);
        return context;
    }
}
