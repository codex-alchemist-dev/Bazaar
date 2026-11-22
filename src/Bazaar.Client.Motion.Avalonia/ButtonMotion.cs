using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;

namespace Bazaar.Client.Motion.Avalonia;

/// <summary>
/// Gives a button the press squash (or darken) and the touch ripple of doc 93, and for a send button the fill and launch.
/// It wraps the button's content in a clipped grid with the overlays the animators need (accent fill, tint, ripple ring), so the
/// view declares only the button. Brushes come from the theme resources; every number comes from the animators' plans.
/// </summary>
public sealed class ButtonMotion : IDisposable
{
    private readonly Button _button;
    private readonly Grid _root;
    private readonly Border _tint;
    private readonly Ellipse _ring;
    private readonly bool _send;
    private bool _armed;
    private bool _launching;

    private ButtonMotion(Button button, TextBox? draft, MotionSettings settings, Func<Control, IFrameDriver> driverFactory)
    {
        _button = button;
        _send = draft is not null;
        var driver = driverFactory(button);

        // The theme eases transform changes itself; the animators already shape them, so the theme's transitions are switched off.
        button.Transitions = null;

        var original = button.Content;
        _root = new Grid { ClipToBounds = true };

        Border? fill = null;
        if (_send)
        {
            fill = Overlay("SystemControlHighlightAccentBrush");
            _root.Children.Add(fill);
        }

        _root.Children.Add(original as Control ?? new TextBlock
        {
            Text = original?.ToString(),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        });

        _tint = Overlay("SystemControlForegroundBaseHighBrush");
        _ring = new Ellipse { IsHitTestVisible = false, Opacity = 0, HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top };
        _ring.Bind(Shape.FillProperty, _root.GetResourceObservable("SystemControlForegroundBaseHighBrush"));
        _root.Children.Add(_tint);
        _root.Children.Add(_ring);
        button.Content = _root;

        Press = new PressAnimator(button, _tint, settings, driver);
        Ripple = new RippleAnimator(_ring, null, settings, driver);
        button.AddHandler(InputElement.PointerPressedEvent, OnPressed, RoutingStrategies.Tunnel | RoutingStrategies.Bubble, handledEventsToo: true);
        button.AddHandler(InputElement.PointerReleasedEvent, OnReleased, RoutingStrategies.Tunnel | RoutingStrategies.Bubble, handledEventsToo: true);
        button.PointerCaptureLost += OnCaptureLost;

        if (fill is not null && draft is not null)
        {
            Send = new SendButtonAnimator(button, fill, settings, driver);
            Send.Arm(false);
            Send.Completed += OnSendCompleted;
            _draft = draft;
            draft.PropertyChanged += OnDraftChanged;
        }
    }

    private TextBox? _draft;

    /// <summary>The press animator of the button.</summary>
    public PressAnimator Press { get; }

    /// <summary>The ripple animator of the button.</summary>
    public RippleAnimator Ripple { get; }

    /// <summary>The send fill and launch animator; null for a plain button.</summary>
    public SendButtonAnimator? Send { get; }

    /// <summary>A plain pressable button: press and ripple.</summary>
    public static ButtonMotion Attach(Button button, MotionSettings? settings = null, Func<Control, IFrameDriver>? driverFactory = null)
    {
        ArgumentNullException.ThrowIfNull(button);
        return new ButtonMotion(button, null, settings ?? MotionHost.Settings, driverFactory ?? MotionHost.DriverFor);
    }

    /// <summary>A send button: press, ripple, and the fill that follows whether <paramref name="draft"/> has text, then the launch when the host calls <see cref="LaunchIfArmed"/>.</summary>
    public static ButtonMotion AttachSend(Button button, TextBox draft, MotionSettings? settings = null, Func<Control, IFrameDriver>? driverFactory = null)
    {
        ArgumentNullException.ThrowIfNull(button);
        ArgumentNullException.ThrowIfNull(draft);
        return new ButtonMotion(button, draft, settings ?? MotionHost.Settings, driverFactory ?? MotionHost.DriverFor);
    }

    /// <summary>Plays the launch when the draft has text. The host calls it when it sends (button or Enter), before it clears the draft.</summary>
    public void LaunchIfArmed()
    {
        if (Send is null || !_armed)
        {
            return;
        }

        _launching = true;
        Send.Launch();
    }

    public void Dispose()
    {
        _button.RemoveHandler(InputElement.PointerPressedEvent, OnPressed);
        _button.RemoveHandler(InputElement.PointerReleasedEvent, OnReleased);
        _button.PointerCaptureLost -= OnCaptureLost;
        if (_draft is not null)
        {
            _draft.PropertyChanged -= OnDraftChanged;
        }

        Press.Dispose();
        Ripple.Dispose();
        Send?.Dispose();
    }

    private static Border Overlay(string brushResource)
    {
        var overlay = new Border { IsHitTestVisible = false, Opacity = 0 };
        overlay.Bind(Border.BackgroundProperty, overlay.GetResourceObservable(brushResource));
        return overlay;
    }

    private void OnPressed(object? sender, PointerPressedEventArgs e)
    {
        Press.Press();
        var origin = e.GetPosition(_root);
        var size = _root.Bounds.Size;
        var dx = Math.Max(origin.X, size.Width - origin.X);
        var dy = Math.Max(origin.Y, size.Height - origin.Y);
        Ripple.Play(origin, Math.Sqrt((dx * dx) + (dy * dy)));
    }

    private void OnReleased(object? sender, PointerReleasedEventArgs e) => Press.Release();

    private void OnCaptureLost(object? sender, PointerCaptureLostEventArgs e) => Press.Release();

    private void OnDraftChanged(object? sender, AvaloniaPropertyChangedEventArgs e)
    {
        if (e.Property != TextBox.TextProperty)
        {
            return;
        }

        _armed = !string.IsNullOrWhiteSpace(_draft?.Text);

        // The draft is cleared by the send itself; the fill empties only after the launch has finished.
        if (!_launching)
        {
            Send?.Arm(_armed);
        }
    }

    private void OnSendCompleted(object? sender, EventArgs e)
    {
        if (_launching)
        {
            _launching = false;
            Send?.Arm(_armed);
        }
    }
}
