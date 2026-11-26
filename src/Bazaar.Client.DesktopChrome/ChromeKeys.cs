namespace Bazaar.Client.DesktopChrome;

/// <summary>
/// Message ids for the desktop chrome's visible text and accessible names (doc 27, Localisation: resource-file strings). English is the
/// source locale, in <c>Strings/chrome.en.json</c>; every id here must exist there (a test checks it, and checks the reverse).
/// </summary>
public static class ChromeKeys
{
    public const string SidebarName = "chrome.sidebar.name";
    public const string SidebarCollapse = "chrome.sidebar.collapse";
    public const string SidebarExpand = "chrome.sidebar.expand";
    public const string StripName = "chrome.strip.name";
    public const string StripNoGroupChat = "chrome.strip.noGroupChat";
    public const string StripStatus = "chrome.strip.status";
    public const string SheetClose = "chrome.sheet.close";
    public const string SheetGrip = "chrome.sheet.grip";

    /// <summary>ICU message; argument <c>title</c>.</summary>
    public const string SheetOpened = "chrome.sheet.opened";

    /// <summary>ICU message; argument <c>title</c>.</summary>
    public const string SheetClosed = "chrome.sheet.closed";

    /// <summary>The name of the ICU argument both sheet messages take.</summary>
    public const string TitleArgument = "title";

    /// <summary>Every id the chrome uses.</summary>
    public static IReadOnlyList<string> All { get; } =
    [
        SidebarName, SidebarCollapse, SidebarExpand, StripName, StripNoGroupChat, StripStatus, SheetClose, SheetGrip, SheetOpened, SheetClosed,
    ];
}
