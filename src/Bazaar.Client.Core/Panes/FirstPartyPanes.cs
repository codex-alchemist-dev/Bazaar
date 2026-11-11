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
    private const string _kanbanTitle = "RyuseiFuda · Board";

    /// <summary>An empty board: five columns, no cards. Shown when no board snapshot is available.</summary>
    private static readonly JsonElement _emptyKanbanModel = JsonElement.Parse("""{"cards":[]}""");

    /// <summary>An empty GC model. The GC pane takes its content from the chat, not the descriptor.</summary>
    private static readonly JsonElement _emptyGcModel = JsonElement.Parse("{}");
    /// <summary>An empty Constellation model: no graph loaded yet.</summary>
    private static readonly JsonElement _emptyConstellationModel = JsonElement.Parse("{}");

    /// <summary>
    /// The first-party pane modules, in registration order. The phone pager lists panes in this order (doc 29).
    /// A head that wants a different set composes its own list with <see cref="PaneRegistry.Compose"/>.
    /// </summary>
    public static IReadOnlyList<IPaneModule> Modules { get; } =
    [
        new KanbanPaneModule(),
        new DashboardPaneModule(),
        new ConstellationPaneModule(),
        new VaultSearchPaneModule(),
        new GcPaneModule(),
    ];

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
        ChatViewModel? chat = null) =>
        PaneRegistry.Compose(Modules, new PaneModuleContext(tiles, vault, chat, report));

    /// <summary>
    /// The panes the Windows head shows when started with <c>--panes</c>: the Kanban board and the Constellation
    /// view, both with empty models. The heads fill the models from the server once a session is connected.
    /// </summary>
    public static IReadOnlyList<PaneDescriptor> WindowPanes() =>
    [
        new PaneDescriptor(PaneKinds.Kanban, _kanbanTitle, string.Empty, _emptyKanbanModel),
        new PaneDescriptor(PaneKinds.Constellation, "Constellation", string.Empty, _emptyConstellationModel),
    ];

    /// <summary>
    /// Pane bodies for the phone pager. The Kanban pane renders its real board from <paramref name="kanbanModel"/>
    /// (the server's board snapshot, Guid-parsed by the pane), or an empty board when none is given. The GC pane
    /// renders the chat through <see cref="PaneViews"/>, or its empty state when no session is connected. Other kinds
    /// return null and keep the pager's kind-name placeholder until their phone views land.
    /// </summary>
    /// <param name="registry">The registry the shell pages through. Its factories build the view models.</param>
    /// <param name="kanbanModel">The board snapshot, or null when no session is connected.</param>
    public static Func<string, Control?> PhonePaneBodies(PaneRegistry registry, JsonElement? kanbanModel = null)
    {
        ArgumentNullException.ThrowIfNull(registry);

        return kind =>
        {
            if (kind == GcPaneViewModel.PaneKind)
            {
                // The GC pane has no model of its own: the registry's chat supplies it, or the pane shows its empty state.
                return PaneViews.Create(registry.Create(new PaneDescriptor(kind, GcPaneViewModel.DefaultTitle, string.Empty, _emptyGcModel)));
            }

            if (kind == PaneKinds.Kanban)
            {
                var model = kanbanModel ?? _emptyKanbanModel;
                return PaneViews.Create(registry.Create(new PaneDescriptor(kind, _kanbanTitle, string.Empty, model)));
            }

            return null;
        };
    }
}
