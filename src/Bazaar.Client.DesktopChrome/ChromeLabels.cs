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

    public string SheetGrip { get; init; } = "Drag to dismiss";

    public static ChromeLabels Default { get; } = new();
}
