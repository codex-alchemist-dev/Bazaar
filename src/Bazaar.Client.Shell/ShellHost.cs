using Avalonia.Controls;
using Bazaar.Accessibility;
using Bazaar.Accessibility.Avalonia;
using Bazaar.Localisation;

namespace Bazaar.Client.Shell;

/// <summary>The result of attaching the shell to a window.</summary>
/// <param name="Content">The control the head sets as the window's content.</param>
/// <param name="Announcer">What feature modules announce through.</param>
public sealed record ShellAttachment(Control Content, IShellAnnouncer Announcer);

/// <summary>
/// The one call a head makes (doc 41c): attaches <see cref="WindowChrome"/> (flow direction from the locale, live
/// region) and returns the wrapped content with the announcer. Composition only; the chrome helper stays the single
/// place that sets direction and mounts the region.
/// </summary>
public static class ShellHost
{
    public static ShellAttachment Attach(
        Control window,
        Control? content,
        ILocaleSource locale,
        AnnouncementGate gate,
        Func<string, int, string>? withHeldBack = null)
    {
        WindowChrome chrome = WindowChrome.Attach(window, locale, gate, withHeldBack);
        return new ShellAttachment(chrome.Wrap(content), new ChromeAnnouncer(chrome));
    }

    /// <summary>
    /// For a head that has already set the content of its window, or of its main view on Android (no window there): moves that content under the chrome and sets it back.
    /// Returns the announcer for feature modules.
    /// </summary>
    public static IShellAnnouncer Install(
        ContentControl window,
        ILocaleSource locale,
        AnnouncementGate gate,
        Func<string, int, string>? withHeldBack = null)
    {
        var existing = window.Content as Control;
        window.Content = null;
        ShellAttachment shell = Attach(window, existing, locale, gate, withHeldBack);
        window.Content = shell.Content;
        return shell.Announcer;
    }

    private sealed class ChromeAnnouncer(WindowChrome chrome) : IShellAnnouncer
    {
        public bool Announce(AnnouncementCategory category, string text) => chrome.Region.Announce(category, text);
    }
}
