namespace Bazaar.Client.DesktopChrome;

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

    /// <summary>Spoken when a sheet opens; {0} is its title.</summary>
    public string SheetOpenedFormat { get; init; } = "{0}, dialog opened";

    /// <summary>Spoken when a sheet closes; {0} is its title.</summary>
    public string SheetClosedFormat { get; init; } = "{0} closed";

    public string SheetGrip { get; init; } = "Drag to dismiss";

    public static ChromeLabels Default { get; } = new();
}
