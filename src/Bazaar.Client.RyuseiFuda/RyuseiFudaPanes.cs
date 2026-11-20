using System.Text.Json;
using Avalonia.Controls;
using Bazaar.Client.Abstractions.Panes;
using Bazaar.Client.Panes.Abstractions;
using Bazaar.Client.RyuseiFuda.Panes;
using Bazaar.Contracts.Panes;

namespace Bazaar.Client.RyuseiFuda;

/// <summary>The RyuseiFuda client module: the pane modules a head composes to ship RyuseiFuda (doc 40, step 7).</summary>
public static class RyuseiFudaPanes
{
    private const string _boardTitle = "RyuseiFuda · Board";

    /// <summary>An empty board: five columns, no cards. Shown when no board snapshot is available.</summary>
    private static readonly JsonElement _emptyBoardModel = JsonElement.Parse("""{"cards":[]}""");

    /// <summary>The board pane with an empty model. A head fills the model from the server once a session is connected.</summary>
    public static PaneDescriptor WindowPane { get; } = new(PaneKinds.Kanban, _boardTitle, string.Empty, _emptyBoardModel);

    /// <summary>The Workday board. Heads list it first, so it opens the phone pager (doc 29).</summary>
    public static IPaneModule Board { get; } = new KanbanPaneModule();

    /// <summary>The Orrery schedule, the Roadmap and the Workday team schedule, in registration order.</summary>
    public static IReadOnlyList<IPaneModule> Modules { get; } =
    [
        new RyuseiSchedulePaneModule(),
        new RoadmapPaneModule(),
        new WorkdayPaneModule(),
    ];

    /// <summary>
    /// The phone body of the board pane, or null for any other kind. It renders the real board from
    /// <paramref name="boardModel"/> (the server's board snapshot), or an empty board when none is given. A head passes it
    /// to <c>FirstPartyPanes.PhonePaneBodies</c>.
    /// </summary>
    public static Control? PhoneBody(string kind, PaneRegistry registry, JsonElement? boardModel = null)
    {
        ArgumentNullException.ThrowIfNull(registry);

        return kind == PaneKinds.Kanban
            ? registry.CreateView(registry.Create(new PaneDescriptor(kind, _boardTitle, string.Empty, boardModel ?? _emptyBoardModel)))
            : null;
    }
}
