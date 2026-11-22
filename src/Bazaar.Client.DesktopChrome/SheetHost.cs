using System.Globalization;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Automation.Peers;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.VisualTree;
using Bazaar.Client.Materials;
using Bazaar.Client.Motion.Avalonia;

namespace Bazaar.Client.DesktopChrome;

/// <summary>How a sheet sits in the window.</summary>
public enum SheetKind
{
    /// <summary>Centred over a scrim; drops in and drags down to dismiss.</summary>
    Modal,

    /// <summary>Docked to the trailing edge; slides in and drags outward to dismiss.</summary>
    Side,
}

/// <summary>
/// The sheet host (doc 93, catalogue row "Bottom sheet", desktop column): one modal or side sheet at a time over a scrim, drawn with
/// the sheet material. It opens with the dialog fade and a short drop or slide (<c>motion.duration.sheet</c> scaled for desktop, the
/// subtle travel token, a critically damped spring so there is no overshoot) and at reduced motion is a fade of 90 ms or less with no
/// travel. Dismiss with Esc, the close button, a press on the scrim, or a drag of the sheet's header past the dismiss fraction.
/// Focus moves into the sheet when it opens, Tab cycles inside it, and focus returns to where it was when it closes.
/// </summary>
public sealed class SheetHost
{
    private readonly ChromeTheme _theme;
    private readonly ChromeMotion _motion;
    private readonly ChromeLabels _labels;
    private readonly ProgressAnimator _open;
    private readonly ProgressAnimator _drag;
    private readonly Grid _root;
    private readonly Border _scrim;
    private readonly Border _panel;
    private readonly Border _header;
    private readonly TextBlock _titleText;
    private readonly Button _close;
    private readonly Border _contentHost;
    private readonly List<(Control Target, bool Enabled, AccessibilityView View)> _inert = [];
    private string _title = string.Empty;
    private SheetKind _kind;
    private IInputElement? _returnFocus;
    private bool _dragging;
    private Point _dragStart;
    private double _dragSign = 1;

    public SheetHost(
        ChromeSurface surface,
        ChromeTheme theme,
        ChromeMotion motion,
        IFrameDriver frames,
        ChromeLabels? labels = null)
    {
        ArgumentNullException.ThrowIfNull(surface);
        ArgumentNullException.ThrowIfNull(theme);
        ArgumentNullException.ThrowIfNull(motion);
        ArgumentNullException.ThrowIfNull(frames);
        _theme = theme;
        _motion = motion;
        _labels = labels ?? ChromeLabels.Default;
        _open = new ProgressAnimator(frames);
        _drag = new ProgressAnimator(frames);
        _open.Changed += (_, _) => ApplyFrame();
        _drag.Changed += (_, _) => ApplyFrame();
        _open.Completed += (_, _) =>
        {
            if (!IsOpen && IsVisible)
            {
                FinishClose();
            }
        };
        _drag.Completed += (_, _) =>
        {
            if (IsOpen && _drag.Value >= 1)
            {
                FinishClose();
            }
        };

        _scrim = new Border();
        _scrim.PointerPressed += (_, e) =>
        {
            if (e.GetCurrentPoint(_scrim).Properties.IsLeftButtonPressed)
            {
                Dismiss();
                e.Handled = true;
            }
        };

        _titleText = new TextBlock { VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis, FontWeight = FontWeight.SemiBold };
        AutomationProperties.SetControlTypeOverride(_titleText, AutomationControlType.Header);
        _close = new Button { Content = new TextBlock { Text = "✕" }, Focusable = true };
        AutomationProperties.SetName(_close, _labels.CloseSheet);
        AutomationProperties.SetAutomationId(_close, "bazaar.sheet.close");
        _close.Click += (_, _) => Dismiss();

        var headerRow = new DockPanel { LastChildFill = true };
        DockPanel.SetDock(_close, Dock.Right);
        headerRow.Children.Add(_close);
        headerRow.Children.Add(_titleText);
        _header = new Border { Child = headerRow };
        AutomationProperties.SetName(_header, _labels.SheetGrip);
        _header.PointerPressed += OnGripPressed;
        _header.PointerMoved += OnGripMoved;
        _header.PointerReleased += OnGripReleased;
        _header.PointerCaptureLost += OnGripCaptureLost;

        _contentHost = new Border();
        var body = new DockPanel { LastChildFill = true };
        DockPanel.SetDock(_header, Dock.Top);
        body.Children.Add(_header);
        body.Children.Add(_contentHost);
        KeyboardNavigation.SetTabNavigation(body, KeyboardNavigationMode.Cycle);

        _panel = surface(MaterialSurface.Sheet, body);
        _panel.ClipToBounds = true;
        AutomationProperties.SetAutomationId(_panel, "bazaar.sheet");
        AutomationProperties.SetControlTypeOverride(_panel, AutomationControlType.Pane);

        _root = new Grid { IsVisible = false };
        _root.Children.Add(_scrim);
        _root.Children.Add(_panel);
        _root.AddHandler(InputElement.KeyDownEvent, OnKeyDown, RoutingStrategies.Tunnel);

        _theme.Changed += (_, _) => ApplyTheme();
        ApplyTheme();
        Arrange(SheetKind.Side);
        ApplyFrame();
    }

    /// <summary>The control to place over the window content, spanning the whole window. It is hidden while no sheet is open.</summary>
    public Control View => _root;

    /// <summary>True from <see cref="Show"/> until a dismissal starts.</summary>
    public bool IsOpen { get; private set; }

    /// <summary>True while a sheet is on screen, including its closing animation.</summary>
    public bool IsVisible => _root.IsVisible;

    /// <summary>The panel, for tests and for a head that needs its bounds.</summary>
    public Border Panel => _panel;

    /// <summary>The scrim behind the panel.</summary>
    public Border Scrim => _scrim;

    /// <summary>The header, which is also the drag grip.</summary>
    public Border Grip => _header;

    public Button CloseButton => _close;

    /// <summary>0 closed to 1 open, as drawn now.</summary>
    public double OpenProgress => _open.Value;

    /// <summary>How far a drag has taken the sheet toward dismissal, 0 to 1.</summary>
    public double DragProgress => _drag.Value;

    /// <summary>Says when a sheet opens and closes. Optional; the shell sets it once its live region exists.</summary>
    public ISheetAnnouncer? Announcer { get; set; }

    /// <summary>
    /// The controls behind the sheet. While a sheet is on screen they are disabled (no focus, no Tab, no pointer input) and hidden
    /// from the accessibility tree; their earlier state is restored when it closes.
    /// </summary>
    public IList<Control> Background { get; } = [];

    /// <summary>Raised after a sheet has closed and focus has gone back.</summary>
    public event EventHandler? Closed;

    /// <summary>Opens a sheet showing <paramref name="content"/>. A sheet already open is replaced in place; focus returns to where it was before the first one.</summary>
    public void Show(Control content, string title, SheetKind kind = SheetKind.Side)
    {
        ArgumentNullException.ThrowIfNull(content);
        ArgumentNullException.ThrowIfNull(title);

        bool wasVisible = _root.IsVisible;
        if (!wasVisible)
        {
            _returnFocus = TopLevel.GetTopLevel(_root)?.FocusManager?.GetFocusedElement();
        }

        _kind = kind;
        _title = title;
        _contentHost.Child = content;
        _titleText.Text = title;
        AutomationProperties.SetName(_panel, title);
        Arrange(kind);

        IsOpen = true;
        _root.IsVisible = true;
        if (!wasVisible)
        {
            MakeBackgroundInert();
            Announcer?.Announce(string.Format(CultureInfo.CurrentCulture, _labels.SheetOpenedFormat, title));
        }

        _drag.Snap(0);
        if (!wasVisible)
        {
            _open.Snap(0);
        }

        _open.AnimateTo(1, _motion.Sheet, _motion.UsesSpring);
        FocusInto(content);
    }

    /// <summary>Starts closing the sheet. Does nothing when none is open.</summary>
    public void Dismiss()
    {
        if (!IsOpen)
        {
            return;
        }

        IsOpen = false;
        _open.AnimateTo(0, _motion.Sheet, _motion.UsesSpring);
    }

    private void FinishClose()
    {
        _open.Snap(0);
        _drag.Snap(0);
        IsOpen = false;
        _root.IsVisible = false;
        _contentHost.Child = null;
        RestoreBackground();
        Announcer?.Announce(string.Format(CultureInfo.CurrentCulture, _labels.SheetClosedFormat, _title));
        IInputElement? target = _returnFocus;
        _returnFocus = null;
        target?.Focus();
        Closed?.Invoke(this, EventArgs.Empty);
    }

    private void MakeBackgroundInert()
    {
        _inert.Clear();
        foreach (Control target in Background)
        {
            _inert.Add((target, target.IsEnabled, AutomationProperties.GetAccessibilityView(target)));
            target.IsEnabled = false;
            AutomationProperties.SetAccessibilityView(target, AccessibilityView.Raw);
        }
    }

    private void RestoreBackground()
    {
        foreach ((Control target, bool enabled, AccessibilityView view) in _inert)
        {
            target.IsEnabled = enabled;
            AutomationProperties.SetAccessibilityView(target, view);
        }

        _inert.Clear();
    }

    private void OnKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape && IsOpen)
        {
            Dismiss();
            e.Handled = true;
        }
    }

    private void OnGripPressed(object? sender, PointerPressedEventArgs e)
    {
        if (!IsOpen || !e.GetCurrentPoint(_header).Properties.IsLeftButtonPressed)
        {
            return;
        }

        _dragSign = OutwardSign();
        _dragging = true;
        _dragStart = e.GetPosition(_root);
        _drag.Snap(0);
        e.Pointer.Capture(_header);
        e.Handled = true;
    }

    private void OnGripMoved(object? sender, PointerEventArgs e)
    {
        if (!_dragging)
        {
            return;
        }

        Point now = e.GetPosition(_root);
        double delta = _kind == SheetKind.Side ? (now.X - _dragStart.X) * _dragSign : now.Y - _dragStart.Y;
        double extent = _kind == SheetKind.Side ? _panel.Bounds.Width : _panel.Bounds.Height;
        _drag.Snap(extent > 0 ? Math.Clamp(delta / extent, 0, 1) : 0);
        e.Handled = true;
    }

    private void OnGripReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (!_dragging)
        {
            return;
        }

        _dragging = false;
        e.Pointer.Capture(null);
        SettleDrag();
        e.Handled = true;
    }

    private void OnGripCaptureLost(object? sender, PointerCaptureLostEventArgs e)
    {
        if (_dragging)
        {
            _dragging = false;
            SettleDrag();
        }
    }

    // Past the fraction the sheet goes the rest of the way and closes; short of it, it comes back.
    private void SettleDrag()
    {
        bool dismiss = _drag.Value >= _theme.Number("gesture.sheet.dismiss.fraction");
        _drag.AnimateTo(dismiss ? 1 : 0, _motion.Sheet, _motion.UsesSpring);
    }

    private void ApplyFrame()
    {
        double open = Math.Clamp(_open.Value, 0, 1);
        double drag = Math.Clamp(_drag.Value, 0, 1);
        _scrim.Opacity = open * (1 - drag);
        _panel.Opacity = open;

        if (!_motion.Travels && drag == 0)
        {
            _panel.RenderTransform = null;
            return;
        }

        double enter = (1 - open) * _motion.Travel;
        if (_kind == SheetKind.Side)
        {
            _panel.RenderTransform = new TranslateTransform(OutwardSign() * (enter + (drag * _panel.Bounds.Width)), 0);
        }
        else
        {
            _panel.RenderTransform = new TranslateTransform(0, -enter + (drag * _panel.Bounds.Height));
        }
    }

    // Which way is outward for a side sheet: the panel's side of the window decides, so it holds in a right-to-left layout too.
    private double OutwardSign() =>
        _panel.Bounds.Width > 0 && _panel.Bounds.Center.X < _root.Bounds.Width / 2 ? -1 : 1;

    private void Arrange(SheetKind kind)
    {
        double unit = _theme.Length("space.lg");
        if (kind == SheetKind.Side)
        {
            _panel.HorizontalAlignment = HorizontalAlignment.Right;
            _panel.VerticalAlignment = VerticalAlignment.Stretch;
            _panel.Width = _theme.Length("layout.sheet.side.width");
            _panel.Height = double.NaN;
            _panel.Margin = default;
            _panel.CornerRadius = new CornerRadius(_theme.Length("radius.md"), 0, 0, _theme.Length("radius.md"));
        }
        else
        {
            _panel.HorizontalAlignment = HorizontalAlignment.Center;
            _panel.VerticalAlignment = VerticalAlignment.Center;
            _panel.Width = _theme.Length("layout.sheet.width");
            _panel.Height = double.NaN;
            _panel.Margin = new Thickness(unit);
            _panel.CornerRadius = new CornerRadius(_theme.Length("radius.md"));
        }
    }

    private void ApplyTheme()
    {
        double sm = _theme.Length("space.sm");
        double md = _theme.Length("space.md");
        _scrim.Background = _theme.Brush("color.background.editor", _theme.Number("material.scrim.opacity"));
        _panel.BorderBrush = _theme.Brush("color.border.default");
        _panel.BorderThickness = new Thickness(1);
        _header.Padding = new Thickness(md, sm);
        _header.Background = _theme.Brush("color.background.surface", 0);
        _contentHost.Padding = new Thickness(md);
        _titleText.Foreground = _theme.Brush("color.text.primary");
        _titleText.FontFamily = _theme.Family("font.family.ui");
        _titleText.FontSize = _theme.Length("font.size.body");
        _close.Foreground = _theme.Brush("color.text.primary");
        _close.CornerRadius = new CornerRadius(_theme.Length("radius.sm"));
        _contentHost.IsVisible = true;
        Arrange(_kind);
    }

    // First focusable control in the sheet's content; the close button when the content has none.
    private void FocusInto(Control content)
    {
        InputElement? target = content is InputElement { Focusable: true, IsEffectivelyEnabled: true } self
            ? self
            : content.GetVisualDescendants().OfType<InputElement>().FirstOrDefault(e => e.Focusable && e.IsEffectivelyEnabled && e.IsEffectivelyVisible && KeyboardNavigation.GetIsTabStop(e));
        (target ?? _close).Focus();
    }
}
