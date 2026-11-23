using Avalonia;
using Avalonia.Automation;
using Avalonia.Automation.Peers;
using Avalonia.Controls;
using Avalonia.Media;
using Bazaar.Client.Materials;
using Bazaar.Client.Motion.Avalonia;

namespace Bazaar.Client.DesktopChrome;

/// <summary>
/// Draws a chrome surface: the head passes <c>ShellMaterials.Surface</c>, which wraps the content in the material for the surface
/// kind and keeps it current when the OS or the person's setting changes. Content surfaces throw there, and nothing here asks for one.
/// </summary>
public delegate Border ChromeSurface(MaterialSurface surface, Control? content);

/// <summary>
/// The desktop sidebar (doc 93, Desktop and phone split: the GC rail): a translucent chrome surface with a toggle that collapses it to
/// a narrow rail and a slot for the group chat list, which this module does not own. Keyboard operable: the toggle is the first tab stop,
/// takes Enter and Space, and a collapsed sidebar takes its list out of the tab order. No chord is bound; the head maps its mnemonic to
/// <see cref="Toggle"/>. The list fades and slides on <c>motion.duration.tab</c> with a critically damped spring (no overshoot) while the width snaps at the start of an expand or the end of a collapse (doc 93: opacity and transform only); reduced motion snaps everything.
/// </summary>
public sealed class DesktopSidebar
{
    private readonly ChromeTheme _theme;
    private readonly ChromeMotion _motion;
    private readonly ChromeLabels _labels;
    private readonly ProgressAnimator _progress;
    private readonly Border _surface;
    private readonly Button _toggle;
    private readonly TextBlock _glyph;

    public DesktopSidebar(
        ChromeSurface surface,
        ChromeTheme theme,
        ChromeMotion motion,
        IFrameDriver frames,
        ChromeLabels? labels = null,
        bool expanded = true)
    {
        ArgumentNullException.ThrowIfNull(surface);
        ArgumentNullException.ThrowIfNull(theme);
        ArgumentNullException.ThrowIfNull(motion);
        ArgumentNullException.ThrowIfNull(frames);
        _theme = theme;
        _motion = motion;
        _labels = labels ?? ChromeLabels.Default;
        IsExpanded = expanded;
        _progress = new ProgressAnimator(frames, expanded ? 1 : 0);
        _progress.Changed += (_, _) => ApplyProgress();

        _glyph = new TextBlock { Text = "≡", HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Center, VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center };
        _toggle = new Button
        {
            Content = _glyph,
            HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Left,
            Focusable = true,
        };
        _toggle.Click += (_, _) => Toggle();
        AutomationProperties.SetAutomationId(_toggle, "bazaar.sidebar.toggle");

        ListSlot = new Border { ClipToBounds = true };
        AutomationProperties.SetName(ListSlot, _labels.SidebarName);

        var body = new DockPanel { LastChildFill = true };
        DockPanel.SetDock(_toggle, Avalonia.Controls.Dock.Top);
        body.Children.Add(_toggle);
        body.Children.Add(ListSlot);

        _surface = surface(MaterialSurface.Chrome, body);
        _surface.ClipToBounds = true;
        AutomationProperties.SetName(_surface, _labels.SidebarName);
        AutomationProperties.SetAutomationId(_surface, "bazaar.sidebar");
        AutomationProperties.SetControlTypeOverride(_surface, AutomationControlType.Pane);

        _theme.Changed += (_, _) => ApplyTheme();
        ApplyTheme();
        ApplyProgress();
    }

    /// <summary>The control to place in the window.</summary>
    public Control View => _surface;

    /// <summary>The slot for the group chat list. Empty until the Gc module's list is mounted.</summary>
    public Border ListSlot { get; }

    /// <summary>The collapse toggle, first in the tab order.</summary>
    public Button ToggleButton => _toggle;

    /// <summary>Where the sidebar is heading (or has reached): true is expanded.</summary>
    public bool IsExpanded { get; private set; }

    /// <summary>0 collapsed to 1 expanded, as drawn now.</summary>
    public double Progress => _progress.Value;

    public bool IsAnimating => _progress.IsRunning;

    /// <summary>Raised when <see cref="IsExpanded"/> changes.</summary>
    public event EventHandler? ExpandedChanged;

    public void Toggle() => SetExpanded(!IsExpanded);

    public void SetExpanded(bool expanded)
    {
        if (expanded == IsExpanded)
        {
            return;
        }

        IsExpanded = expanded;
        double target = expanded ? 1 : 0;
        if (_motion.Travels)
        {
            _progress.AnimateTo(target, _motion.Tab, _motion.UsesSpring);
        }
        else
        {
            // Reduced motion: nothing resizes in front of the person; the rail changes at once.
            _progress.Snap(target);
        }

        ApplyProgress();
        ApplyLabels();
        ExpandedChanged?.Invoke(this, EventArgs.Empty);
    }

    // Doc 93: only opacity and transform animate. The layout width never tweens: it snaps to the expanded width when an expand
    // starts and to the collapsed width when a collapse has finished, and the list fades and slides in between.
    private void ApplyProgress()
    {
        double v = Math.Clamp(_progress.Value, 0, 1);
        bool wide = IsExpanded || v > 0;
        _surface.Width = _theme.Length(wide ? "layout.sidebar.width.expanded" : "layout.sidebar.width.collapsed");
        ListSlot.Opacity = v;
        ListSlot.RenderTransform = v >= 1 || !_motion.Travels ? null : new TranslateTransform(-(1 - v) * _motion.Travel, 0);

        // A collapsed list leaves the tab order and the accessibility tree.
        ListSlot.IsVisible = v > 0;
    }

    private void ApplyTheme()
    {
        double sm = _theme.Length("space.sm");
        _surface.CornerRadius = default;
        _surface.BorderBrush = _theme.Brush("color.border.default");
        _surface.BorderThickness = new Thickness(0, 0, 1, 0);
        _toggle.Padding = new Thickness(sm);
        _toggle.Margin = new Thickness(sm);
        _toggle.CornerRadius = new CornerRadius(_theme.Length("radius.sm"));
        _toggle.Foreground = _theme.Brush("color.text.primary");
        _glyph.Foreground = _theme.Brush("color.text.primary");
        _glyph.FontSize = _theme.Length("font.size.body");
        _glyph.FontFamily = _theme.Family("font.family.ui");
        ApplyProgress();
        ApplyLabels();
    }

    private void ApplyLabels() =>
        AutomationProperties.SetName(_toggle, IsExpanded ? _labels.CollapseSidebar : _labels.ExpandSidebar);
}
