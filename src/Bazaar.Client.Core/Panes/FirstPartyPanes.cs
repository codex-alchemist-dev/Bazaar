using System.Text.Json;
using Avalonia.Controls;
using Bazaar.Client.Core.Chat;
using Bazaar.Client.Core.Constellation;
using Bazaar.Client.Core.Server;
using Bazaar.Client.Core.Vault;
using Bazaar.Contracts.Panes;

namespace Bazaar.Client.Core.Panes;

/// <summary>Registers the panes every client ships with.</summary>
public static class FirstPartyPanes
{
    private const string _kanbanTitle = "Kanban";

    /// <summary>An empty board: five columns, no cards. Shown when no board snapshot is available.</summary>
    private static readonly JsonElement _emptyKanbanModel = JsonElement.Parse("""{"cards":[]}""");

    // TODO(component): the kernel (Bazaar.Server.Components, IBazaarComponent) is server-side and the client has no DI
    // container yet. Decide how client components register (a head composition root or a client kernel), then turn this
    // into per-component registrations. Until then the heads call CreateRegistry.
    /// <param name="report">Called once per kind that falls back to the generic view.</param>
    /// <param name="tiles">The Constellation tile source the heads inject. Null leaves the pane without tile requests.</param>
    /// <param name="vault">The Vault API to use. Null builds one for <see cref="ClientServer.Current"/>.</param>
    /// <param name="chat">The group chat the GC pane shows. Null when no session is connected; the pane then shows its empty state.</param>
    public static PaneRegistry CreateRegistry(
        Action<string>? report = null,
        IConstellationTileSource? tiles = null,
        VaultApi? vault = null,
        ChatViewModel? chat = null)
    {
        var registry = new PaneRegistry(report);
        registry.Register(PaneKinds.Kanban, descriptor => new KanbanPaneViewModel(descriptor));
        registry.Register(PaneKinds.Dashboard, descriptor => new DashboardPaneViewModel(descriptor));
        registry.Register(PaneKinds.Constellation, descriptor =>
            new ConstellationPaneViewModel(descriptor, tiles, ConstellationPaneViewModel.DefaultNodeCap));
        var vaultApi = vault ?? VaultApiFactory.Create(ClientServer.Current);
        registry.Register(PaneKinds.VaultSearch, descriptor => new VaultSearchPaneViewModel(descriptor, vaultApi));

        registry.Register(GcPaneViewModel.PaneKind, descriptor => new GcPaneViewModel(descriptor, chat));
        return registry;
    }

    /// <summary>
    /// Pane bodies for the phone pager. The Kanban pane renders its real board from <paramref name="kanbanModel"/>
    /// (the server's board snapshot, Guid-parsed by the pane), or an empty board when none is given. Other kinds
    /// return null and keep the pager's kind-name placeholder until their phone views land.
    /// </summary>
    /// <param name="registry">The registry the shell pages through. Its factories build the view models.</param>
    /// <param name="kanbanModel">The board snapshot, or null when no session is connected.</param>
    public static Func<string, Control?> PhonePaneBodies(PaneRegistry registry, JsonElement? kanbanModel = null)
    {
        ArgumentNullException.ThrowIfNull(registry);

        return kind =>
        {
            if (kind != PaneKinds.Kanban)
            {
                return null;
            }

            var model = kanbanModel ?? _emptyKanbanModel;
            return PaneViews.Create(registry.Create(new PaneDescriptor(kind, _kanbanTitle, string.Empty, model)));
        };
    }
}
