using Bazaar.Client.Abstractions.Panes;
using Bazaar.Client.Core.Chat;
using Bazaar.Client.Core.Constellation;
using Bazaar.Client.Core.Panes;
using Bazaar.Client.RyuseiFuda;
using Bazaar.Client.Vault;

namespace Bazaar.Client.Core.Tests.Panes;

/// <summary>The pane modules a full head ships, composed the way the Windows and Android heads compose them.</summary>
internal static class TestPanes
{
    public static IReadOnlyList<IPaneModule> Modules { get; } =
        [RyuseiFudaPanes.Board, .. FirstPartyPanes.Modules, new VaultSearchPaneModule(), .. RyuseiFudaPanes.Modules];

    public static PaneRegistry CreateRegistry(
        Action<string>? report = null,
        IConstellationTileSource? tiles = null,
        ChatViewModel? chat = null) =>
        FirstPartyPanes.CreateRegistry(Modules, report, tiles, chat);
}
