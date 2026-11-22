namespace Bazaar.Client.DesktopChrome;

/// <summary>
/// Chrome sizes, in units of the theme's <c>space.lg</c> token so they scale with the theme's spacing instead of being pixel
/// literals (the theme has no width tokens; adding some is an open item in doc 93). The head or a Rubedo document may replace the record.
/// </summary>
public sealed record ChromeMetrics
{
    /// <summary>Sidebar width when expanded.</summary>
    public double SidebarExpandedUnits { get; init; } = 16;

    /// <summary>Sidebar width when collapsed to its toggle.</summary>
    public double SidebarCollapsedUnits { get; init; } = 3;

    /// <summary>Top strip height.</summary>
    public double StripHeightUnits { get; init; } = 3;

    /// <summary>Width of a side sheet.</summary>
    public double SideSheetUnits { get; init; } = 24;

    /// <summary>Width of a modal sheet.</summary>
    public double ModalSheetUnits { get; init; } = 28;

    /// <summary>Opacity of the scrim behind a sheet at full open.</summary>
    public double ScrimOpacity { get; init; } = 0.4;

    /// <summary>Share of a sheet's extent a drag must pass, on release, to dismiss it.</summary>
    public double DismissDragFraction { get; init; } = 0.3;

    public ChromeMetrics Clamped() => this with
    {
        SidebarExpandedUnits = Math.Max(SidebarExpandedUnits, 1),
        SidebarCollapsedUnits = Math.Clamp(SidebarCollapsedUnits, 1, Math.Max(SidebarExpandedUnits, 1)),
        StripHeightUnits = Math.Max(StripHeightUnits, 1),
        SideSheetUnits = Math.Max(SideSheetUnits, 1),
        ModalSheetUnits = Math.Max(ModalSheetUnits, 1),
        ScrimOpacity = Math.Clamp(ScrimOpacity, 0.0, 1.0),
        DismissDragFraction = Math.Clamp(DismissDragFraction, 0.05, 0.95),
    };

    public static ChromeMetrics Default { get; } = new();
}

/// <summary>Accessible names and visible text of the chrome. English defaults; a head passes localised text from its locale source.</summary>
public sealed record ChromeLabels
{
    public string SidebarName { get; init; } = "Group chats";

    public string CollapseSidebar { get; init; } = "Collapse sidebar";

    public string ExpandSidebar { get; init; } = "Expand sidebar";

    public string NoGroupChat { get; init; } = "No group chat selected";

    public string StripName { get; init; } = "Current group chat";

    public string StatusName { get; init; } = "Status";

    public string CloseSheet { get; init; } = "Close";

    public string SheetGrip { get; init; } = "Drag to dismiss";

    public static ChromeLabels Default { get; } = new();
}
