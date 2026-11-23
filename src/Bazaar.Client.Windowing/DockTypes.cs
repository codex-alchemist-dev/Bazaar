namespace Bazaar.Client.Windowing;

/// <summary>A node of a dock layout (doc 27 layout containers: split, tabs, pane). Immutable; <see cref="DockOps"/> returns new trees.</summary>
public abstract record DockNode;

/// <summary>One pane leaf. <see cref="PaneKind"/> is a registered pane kind (doc 41); <see cref="PaneId"/> is unique within a layout.</summary>
public sealed record DockPane(string PaneKind, string PaneId) : DockNode;

/// <summary>A tab group of pane leaves. <see cref="ActiveIndex"/> is always within <see cref="Tabs"/> and Tabs is never empty.</summary>
public sealed record DockTabs(IReadOnlyList<DockPane> Tabs, int ActiveIndex) : DockNode;

/// <summary>Two children side by side (<see cref="DockOrientation.Horizontal"/>) or stacked (<see cref="DockOrientation.Vertical"/>). Ratio is the first child's share, within 0.1 to 0.9.</summary>
public sealed record DockSplit(DockOrientation Orientation, double Ratio, DockNode First, DockNode Second) : DockNode;

public enum DockOrientation
{
    Horizontal,
    Vertical,
}

/// <summary>Where a dropped pane lands relative to the target pane. Center joins the target's tab group.</summary>
public enum DockEdge
{
    Center,
    Left,
    Right,
    Top,
    Bottom,
}

/// <summary>Result of taking a pane out of a layout. <see cref="Remaining"/> is null when the layout is now empty; <see cref="Pane"/> is null when the id was not found (and Remaining is the unchanged layout).</summary>
public sealed record DockDetachResult(DockNode? Remaining, DockPane? Pane);
