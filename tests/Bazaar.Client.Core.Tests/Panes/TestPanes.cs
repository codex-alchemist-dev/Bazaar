using Bazaar.Client.Abstractions.Panes;
using Bazaar.Client.Constellation;
using Bazaar.Client.Core.Chat;
using Bazaar.Client.Core.Panes;
using Bazaar.Client.Core.Vault;
using Bazaar.Client.RyuseiFuda;

namespace Bazaar.Client.Core.Tests.Panes;

/// <summary>The pane modules a full head ships, composed the way the Windows and Android heads compose them.</summary>
internal static class TestPanes
{
    public static IReadOnlyList<IPaneModule> Modules { get; } =
    [
        RyuseiFudaPanes.Board,
        .. FirstPartyPanes.Modules.Take(1),
        new ConstellationPaneModule(),
        .. FirstPartyPanes.Modules.Skip(1),
        .. RyuseiFudaPanes.Modules,
    ];

    public static PaneRegistry CreateRegistry(
        Action<string>? report = null,
        IConstellationTileSource? tiles = null,
        VaultApi? vault = null,
        ChatViewModel? chat = null) =>
        FirstPartyPanes.CreateRegistry(
            Modules,
            report,
            vault,
            chat,
            tiles is null ? null : new PaneModuleContext().With<IConstellationTileSource>(tiles));
}
