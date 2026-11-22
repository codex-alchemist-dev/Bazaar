using Avalonia.Controls;
using Avalonia.Media;
using Bazaar.Localisation;

namespace Bazaar.Accessibility.Avalonia;

/// <summary>
/// Window-level accessibility chrome (docs/design/41c-window-chrome-host.md, PROPOSED): sets the window's
/// <see cref="Window.FlowDirection"/> from the active locale and keeps it current when the locale changes, and hosts the
/// rate-limited live region. A head calls <see cref="Attach"/> once after creating its main window, then passes its
/// content through <see cref="Wrap"/>.
/// </summary>
public sealed class WindowChrome
{
    private readonly Window _window;
    private readonly ILocaleSource _locale;

    private WindowChrome(Window window, ILocaleSource locale, GatedLiveRegion region)
    {
        _window = window;
        _locale = locale;
        Region = region;
    }

    /// <summary>The window's named focus scopes; the head registers them in reading order.</summary>
    public FocusScopes Scopes { get; } = new();

    /// <summary>The live region; modules announce through <see cref="GatedLiveRegion.Announce"/>.</summary>
    public GatedLiveRegion Region { get; }

    /// <summary>Applies the locale's direction to the window now and on every locale change.</summary>
    public static WindowChrome Attach(Window window, ILocaleSource locale, AnnouncementGate gate, Func<string, int, string>? withHeldBack = null)
    {
        ArgumentNullException.ThrowIfNull(window);
        ArgumentNullException.ThrowIfNull(locale);
        ArgumentNullException.ThrowIfNull(gate);

        var chrome = new WindowChrome(window, locale, new GatedLiveRegion(gate, withHeldBack));
        chrome.ApplyDirection();
        locale.LocaleChanged += (_, _) => chrome.ApplyDirection();
        return chrome;
    }

    /// <summary>Returns <paramref name="content"/> together with the live region, to set as the window's content.</summary>
    public Control Wrap(Control? content)
    {
        var host = new Grid();
        if (content is not null)
        {
            host.Children.Add(content);
        }

        host.Children.Add(Region.Control);
        return host;
    }

    private void ApplyDirection() =>
        _window.FlowDirection = TextDirectionOf.ForLocale(_locale.Locale) == TextDirection.RightToLeft
            ? FlowDirection.RightToLeft
            : FlowDirection.LeftToRight;
}
