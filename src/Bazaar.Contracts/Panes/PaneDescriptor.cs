using System.Text.Json;

namespace Bazaar.Contracts.Panes;

/// <summary>
/// A declarative pane the server sends to clients (doc 20). The client maps <see cref="Kind"/> to a
/// view model; an unknown kind falls back to the generic tree view. The model is data, never code.
/// </summary>
/// <param name="Kind">Namespaced pane kind, for example <see cref="PaneKinds.Kanban"/> or <c>godot.scene-tree</c>.</param>
/// <param name="Title">Heading shown on the pane.</param>
/// <param name="Capability">Capability that backs the pane, or an empty string for first-party panes.</param>
/// <param name="Model">Kind-specific payload.</param>
public sealed record PaneDescriptor(string Kind, string Title, string Capability, JsonElement Model);

/// <summary>Kinds of the first-party panes every client ships with.</summary>
public static class PaneKinds
{
    /// <summary>Kanban dependency board (doc 12).</summary>
    public const string Kanban = "bazaar.kanban";

    /// <summary>Start-screen dashboard of what changed while away (CLAUDE.md, client start screen).</summary>
    public const string Dashboard = "bazaar.dashboard";

    /// <summary>Constellation 2D view of the Vault's knowledge graph (doc 19).</summary>
    public const string Constellation = "bazaar.constellation";
}
