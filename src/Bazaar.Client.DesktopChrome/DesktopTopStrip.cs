using Avalonia;
using Avalonia.Automation;
using Avalonia.Automation.Peers;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Bazaar.Client.Materials;
using Bazaar.Client.Motion.Avalonia;

namespace Bazaar.Client.DesktopChrome;

/// <summary>
/// The desktop top strip (doc 93): a translucent chrome surface with the current group chat's name on the leading side and a slot for
/// status on the trailing side. The name cross-fades when the group chat changes (<c>motion.duration.tab</c>, or the 90 ms reduced fade).
/// What goes in the status slot is the head's business; the strip only lays it out.
/// </summary>
public sealed class DesktopTopStrip
{
    private readonly ChromeTheme _theme;
    private readonly ChromeMotion _motion;
    private readonly ChromeMetrics _metrics;
    private readonly ChromeLabels _labels;
    private readonly ProgressAnimator _fade;
    private readonly Border _surface;
    private readonly TextBlock _title;
    private string? _groupChatName;

    public DesktopTopStrip(
        ChromeSurface surface,
        ChromeTheme theme,
        ChromeMotion motion,
        IFrameDriver frames,
        ChromeMetrics? metrics = null,
        ChromeLabels? labels = null)
    {
        ArgumentNullException.ThrowIfNull(surface);
        ArgumentNullException.ThrowIfNull(theme);
        ArgumentNullException.ThrowIfNull(motion);
        ArgumentNullException.ThrowIfNull(frames);
        _theme = theme;
        _motion = motion;
        _metrics = (metrics ?? ChromeMetrics.Default).Clamped();
        _labels = labels ?? ChromeLabels.Default;
        _title = new TextBlock { VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis, FontWeight = FontWeight.SemiBold };
        AutomationProperties.SetControlTypeOverride(_title, AutomationControlType.Header);
        AutomationProperties.SetAutomationId(_title, "bazaar.strip.title");
        _fade = new ProgressAnimator(frames, 1);
        _fade.Changed += (_, _) => _title.Opacity = _fade.Value;

        StatusSlot = new Border { VerticalAlignment = VerticalAlignment.Center };
        AutomationProperties.SetName(StatusSlot, _labels.StatusName);
        AutomationProperties.SetAutomationId(StatusSlot, "bazaar.strip.status");

        var row = new DockPanel { LastChildFill = true };
        DockPanel.SetDock(StatusSlot, Dock.Right);
        row.Children.Add(StatusSlot);
        row.Children.Add(_title);

        _surface = surface(MaterialSurface.Strip, row);
        AutomationProperties.SetName(_surface, _labels.StripName);
        AutomationProperties.SetAutomationId(_surface, "bazaar.strip");
        AutomationProperties.SetControlTypeOverride(_surface, AutomationControlType.Pane);

        _theme.Changed += (_, _) => ApplyTheme();
        ApplyTheme();
        ShowTitle();
    }

    /// <summary>The control to place in the window.</summary>
    public Control View => _surface;

    /// <summary>The trailing slot for status chips and indicators.</summary>
    public Border StatusSlot { get; }

    /// <summary>The visible title text, which is the group chat name or the placeholder.</summary>
    public string TitleText => _title.Text ?? string.Empty;

    /// <summary>The current group chat's name; null or empty shows the placeholder.</summary>
    public string? GroupChatName
    {
        get => _groupChatName;
        set
        {
            if (string.Equals(_groupChatName, value, StringComparison.Ordinal))
            {
                return;
            }

            _groupChatName = value;
            ShowTitle();
            _fade.Snap(0);
            _fade.AnimateTo(1, _motion.Tab, _motion.UsesSpring);
        }
    }

    private void ShowTitle()
    {
        _title.Text = string.IsNullOrWhiteSpace(_groupChatName) ? _labels.NoGroupChat : _groupChatName;
        AutomationProperties.SetName(_title, _title.Text);
    }

    private void ApplyTheme()
    {
        double unit = _theme.Length("space.lg");
        double md = _theme.Length("space.md");
        _surface.Height = _metrics.StripHeightUnits * unit;
        _surface.Padding = new Thickness(md, 0);
        _surface.BorderBrush = _theme.Brush("color.border.default");
        _surface.BorderThickness = new Thickness(0, 0, 0, 1);
        _title.Foreground = _theme.Brush("color.text.primary");
        _title.FontFamily = _theme.Family("font.family.ui");
        _title.FontSize = _theme.Length("font.size.body");
    }
}
