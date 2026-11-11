using Bazaar.Client.Core.Chat;
using Bazaar.Client.Core.Constellation;
using Bazaar.Client.Core.Vault;

namespace Bazaar.Client.Core.Panes;

/// <summary>
/// The optional dependencies the heads inject when they build a pane registry. Every member may be null; a module
/// decides what a null means for its pane.
/// </summary>
/// <param name="Tiles">The Constellation tile source. Null leaves the pane without tile requests.</param>
/// <param name="Vault">The Vault API. Null builds one for the current client server.</param>
/// <param name="Chat">The group chat the GC pane shows. Null shows the pane's empty state.</param>
/// <param name="Report">Called once per kind that falls back to the generic view.</param>
public sealed record PaneModuleContext(
    IConstellationTileSource? Tiles = null,
    VaultApi? Vault = null,
    ChatViewModel? Chat = null,
    Action<string>? Report = null);
