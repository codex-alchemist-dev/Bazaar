using Avalonia;
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
    /// <summary>
    /// Marks a control (an arrow, a back chevron, a send glyph) that must be mirrored in right-to-left layouts (doc 49
    /// section 2). While the control's effective flow direction is right-to-left it is flipped horizontally; left-to-right
    /// shows it unchanged. Icons that read the same either way (a clock, a gear) are simply not marked.
    /// </summary>
    public static readonly AttachedProperty<bool> MirrorsInRtlProperty =
        AvaloniaProperty.RegisterAttached<WindowChrome, Control, bool>("MirrorsInRtl");

    static WindowChrome() =>
        MirrorsInRtlProperty.Changed.AddClassHandler<Control>((control, args) =>
        {
            ApplyMirror(control);
            if (args.NewValue is true)
            {
                control.PropertyChanged += OnMarkedControlPropertyChanged;
            }
            else
            {
                control.PropertyChanged -= OnMarkedControlPropertyChanged;
            }
        });

    private readonly Control _host;
    private readonly ILocaleSource _locale;

    private WindowChrome(Control host, ILocaleSource locale, GatedLiveRegion region)
    {
        _host = host;
        _locale = locale;
        Region = region;
    }

    /// <summary>The window's named focus scopes; the head registers them in reading order.</summary>
    public FocusScopes Scopes { get; } = new();

    /// <summary>Scales the wrapped content (doc 49: at least 200 percent); the head sets it from the user's setting.</summary>
    public TextScaler TextScale { get; } = new();

    /// <summary>The reduced-motion rule for this window; the head sets <see cref="MotionPolicy.Reduced"/> from the theme's reducedMotion flag.</summary>
    public MotionPolicy Motion { get; } = new();

    /// <summary>The live region; modules announce through <see cref="GatedLiveRegion.Announce"/>.</summary>
    public GatedLiveRegion Region { get; }

    /// <summary>Applies the locale's direction to the host (a <see cref="Window"/>, or the main view on Android) now and on every locale change.</summary>
    public static WindowChrome Attach(Control window, ILocaleSource locale, AnnouncementGate gate, Func<string, int, string>? withHeldBack = null)
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
        TextScale.SetContent(content);
        host.Children.Add(TextScale.Host);
        host.Children.Add(Region.Control);
        return host;
    }

    public static bool GetMirrorsInRtl(Control control) => control.GetValue(MirrorsInRtlProperty);

    public static void SetMirrorsInRtl(Control control, bool value) => control.SetValue(MirrorsInRtlProperty, value);

    private static void OnMarkedControlPropertyChanged(object? sender, AvaloniaPropertyChangedEventArgs e)
    {
        if (e.Property == Visual.FlowDirectionProperty && sender is Control control)
        {
            ApplyMirror(control);
        }
    }

    private static void ApplyMirror(Control control)
    {
        var mirrored = GetMirrorsInRtl(control) && control.FlowDirection == FlowDirection.RightToLeft;
        control.RenderTransformOrigin = RelativePoint.Center;
        control.RenderTransform = mirrored ? new ScaleTransform(-1, 1) : null;
    }

    private void ApplyDirection() =>
        _host.FlowDirection = TextDirectionOf.ForLocale(_locale.Locale) == TextDirection.RightToLeft
            ? FlowDirection.RightToLeft
            : FlowDirection.LeftToRight;
}
