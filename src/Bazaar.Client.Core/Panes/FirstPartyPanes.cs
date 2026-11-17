using System.Text.Json;
using Avalonia.Controls;
using Bazaar.Client.Abstractions.Panes;
using Bazaar.Client.Abstractions.Server;
using Bazaar.Client.Core.Chat;
using Bazaar.Client.Core.Constellation;
using Bazaar.Client.Core.Vault;
using Bazaar.Client.Panes.Abstractions;
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
    /// The pane modules Client.Core itself ships, in registration order. A head composes them with the feature modules
    /// it ships (for example <c>Bazaar.Client.RyuseiFuda</c>) and passes the full list to <see cref="CreateRegistry"/>.
    /// The phone pager lists panes in the order of that list (doc 29).
    /// </summary>
    public static IReadOnlyList<IPaneModule> Modules { get; } =
    [
        new DashboardPaneModule(),
        new ConstellationPaneModule(),
        new VaultSearchPaneModule(),
        new GcPaneModule(),
    ];

    /// <param name="modules">The pane modules the head ships, in pager order.</param>
    /// <param name="report">Called once per kind that falls back to the generic view.</param>
    /// <param name="tiles">The Constellation tile source the heads inject. Null leaves the pane without tile requests.</param>
    /// <param name="vault">The Vault API to use. Null builds one for <see cref="ClientServer.Current"/>.</param>
    /// <param name="chat">The group chat the GC pane shows. Null when no session is connected; the pane then shows its empty state.</param>
    public static PaneRegistry CreateRegistry(
        IEnumerable<IPaneModule> modules,
        Action<string>? report = null,
        IConstellationTileSource? tiles = null,
        VaultApi? vault = null,
        ChatViewModel? chat = null) =>
        PaneRegistry.Compose(PaneViews.Instance, modules, CreateContext(report, tiles, vault, chat));

    /// <summary>Hands each injected feature dependency to the context under its type; an absent one adds nothing.</summary>
    private static PaneModuleContext CreateContext(
        Action<string>? report,
        IConstellationTileSource? tiles,
        VaultApi? vault,
        ChatViewModel? chat)
    {
        var context = new PaneModuleContext(report);
        if (tiles is not null) context = context.With(tiles);
        if (vault is not null) context = context.With(vault);
        if (chat is not null) context = context.With(chat);
        return context;
    }

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
    /// renders the chat through the registry's view, or its empty state when no session is connected. Other kinds
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
                return registry.CreateView(registry.Create(new PaneDescriptor(kind, GcPaneViewModel.DefaultTitle, string.Empty, _emptyGcModel)));
            }

            if (kind == PaneKinds.Kanban)
            {
                var model = kanbanModel ?? _emptyKanbanModel;
                return registry.CreateView(registry.Create(new PaneDescriptor(kind, _kanbanTitle, string.Empty, model)));
            }

            return null;
        };
    }
}
