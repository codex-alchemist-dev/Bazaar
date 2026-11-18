using System.Text.Json;
using Avalonia.Controls;
using Bazaar.Client.Abstractions.Panes;
using Bazaar.Client.Abstractions.Server;
using Bazaar.Client.Panes.Abstractions;
using Bazaar.Contracts.Panes;

namespace Bazaar.Client.Core.Panes;

/// <summary>Registers the panes every client ships with.</summary>
public static class FirstPartyPanes
{
    private const string _kanbanTitle = "RyuseiFuda · Board";

    /// <summary>An empty board: five columns, no cards. Shown when no board snapshot is available.</summary>
    private static readonly JsonElement _emptyKanbanModel = JsonElement.Parse("""{"cards":[]}""");

    /// <summary>An empty Constellation model: no graph loaded yet.</summary>
    private static readonly JsonElement _emptyConstellationModel = JsonElement.Parse("{}");

    /// <summary>
    /// The pane modules Client.Core itself ships, in registration order. A head composes them with the feature modules
    /// it ships (for example <c>Bazaar.Client.RyuseiFuda</c>, <c>Bazaar.Client.Constellation</c> or <c>Bazaar.Client.Gc</c>)
    /// and passes the full list to <see cref="CreateRegistry"/>. The phone pager lists panes in the order of that list (doc 29).
    /// </summary>
    public static IReadOnlyList<IPaneModule> Modules { get; } =
    [
        new DashboardPaneModule(),
    ];

    /// <param name="modules">The pane modules the head ships, in pager order.</param>
    /// <param name="report">Called once per kind that falls back to the generic view.</param>
    /// <param name="features">
    /// The feature dependencies the head injects with <see cref="PaneModuleContext.With{T}"/>, for example the Constellation
    /// tile source or the group chat the GC pane shows. Null adds none.
    /// </param>
    public static PaneRegistry CreateRegistry(
        IEnumerable<IPaneModule> modules,
        Action<string>? report = null,
        PaneModuleContext? features = null) =>
        PaneRegistry.Compose(PaneViews.Instance, modules, CreateContext(report, features));

    /// <summary>Hands the report callback to the context; an absent one adds nothing.</summary>
    private static PaneModuleContext CreateContext(Action<string>? report, PaneModuleContext? features)
    {
        var context = features ?? new PaneModuleContext();
        if (report is not null) context = context with { Report = report };
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
    /// (the server's board snapshot, Guid-parsed by the pane), or an empty board when none is given. Other kinds are
    /// asked of <paramref name="extra"/> (a head passes the feature modules' phone bodies, for example the GC pane's),
    /// and return null, which keeps the pager's kind-name placeholder, when no body is given for them.
    /// </summary>
    /// <param name="registry">The registry the shell pages through. Its factories build the view models.</param>
    /// <param name="kanbanModel">The board snapshot, or null when no session is connected.</param>
    /// <param name="extra">Phone bodies for kinds this class does not own. Null adds none.</param>
    public static Func<string, Control?> PhonePaneBodies(PaneRegistry registry, JsonElement? kanbanModel = null, Func<string, Control?>? extra = null)
    {
        ArgumentNullException.ThrowIfNull(registry);

        return kind =>
        {
            if (kind == PaneKinds.Kanban)
            {
                var model = kanbanModel ?? _emptyKanbanModel;
                return registry.CreateView(registry.Create(new PaneDescriptor(kind, _kanbanTitle, string.Empty, model)));
            }

            return extra?.Invoke(kind);
        };
    }
}
