using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;

namespace Bazaar.Client.Mobile;

/// <summary>What a release in the radial menu asked for.</summary>
/// <param name="Outcome">What the state machine decided.</param>
/// <param name="SectorId">The sector chosen, when <paramref name="Outcome"/> is <see cref="RadialOutcome.OpenSector"/>.</param>
public sealed record RadialRelease(RadialOutcome Outcome, string? SectorId);

/// <summary>
/// Puts the phone gestures over a body (doc 93, Phone gestures): swipe left or right for the neighbouring page, and hold still
/// for 400 ms for the radial menu with dictation at its centre (doc 29). It owns the pointer handling and the overlay; the
/// head decides what each event does. Touches in the screen-edge bands and over selectable text are left to the system and
/// the text box.
/// </summary>
public sealed class PhoneGestureLayer : Grid
{
    private readonly PhoneGestureRecognizer _recognizer;
    private readonly RadialMenu _menu;
    private readonly RadialGeometry _geometry;
    private readonly RadialMenuView _view;
    private readonly DispatcherTimer _holdTimer;
    private GesturePoint _origin;
    private bool _dictating;

    public PhoneGestureLayer(Control body, IReadOnlyList<string>? sectorIds = null, GestureOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(body);
        var ids = sectorIds ?? AndroidSettings.DefaultRadialSectors;
        var gestureOptions = options ?? new GestureOptions();

        _recognizer = new PhoneGestureRecognizer(gestureOptions);
        _menu = new RadialMenu(ids);
        _geometry = new RadialGeometry(ids);
        _view = new RadialMenuView(ids, _geometry) { IsVisible = false };
        _holdTimer = new DispatcherTimer { Interval = gestureOptions.HoldTime };
        _holdTimer.Tick += OnHoldTimer;

        // A transparent background makes empty space hit-testable, so a swipe or hold on it reaches the layer.
        Background = Avalonia.Media.Brushes.Transparent;
        Children.Add(body);
        Children.Add(_view);
        _view.HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Left;
        _view.VerticalAlignment = Avalonia.Layout.VerticalAlignment.Top;

        // Tunnel, so the layer sees every touch before the body's own controls. Handled children still count: a touch on a
        // button can become a swipe, and the layer only captures the pointer once a swipe or hold begins.
        AddHandler(PointerPressedEvent, OnPressed, Avalonia.Interactivity.RoutingStrategies.Tunnel, handledEventsToo: true);
        AddHandler(PointerMovedEvent, OnMoved, Avalonia.Interactivity.RoutingStrategies.Tunnel, handledEventsToo: true);
        AddHandler(PointerReleasedEvent, OnReleased, Avalonia.Interactivity.RoutingStrategies.Tunnel, handledEventsToo: true);
        PointerCaptureLost += OnCaptureLost;
    }

    /// <summary>The swipe moved. The value is the horizontal distance so far; the head shifts the page by it.</summary>
    public event EventHandler<double>? SwipeProgressed;

    /// <summary>The swipe was released far or fast enough.</summary>
    public event EventHandler<SwipeDirection>? SwipeCommitted;

    /// <summary>The swipe ended short, or was interrupted. The head puts the page back.</summary>
    public event EventHandler? SwipeCancelled;

    /// <summary>Dictation should start (true) or stop (false). Raised on every change, so the recorder follows the finger.</summary>
    public event EventHandler<bool>? DictationChanged;

    /// <summary>The radial menu was released. Dictation has already been told to stop.</summary>
    public event EventHandler<RadialRelease>? RadialReleased;

    /// <summary>True while the radial menu is on screen.</summary>
    public bool IsRadialOpen => _menu.Phase == RadialPhase.Open;

    /// <summary>The overlay, for tests and for heads that style it.</summary>
    public RadialMenuView Radial => _view;

    private void OnPressed(object? sender, PointerPressedEventArgs e)
    {
        var point = Position(e);
        var overText = e.Source is Visual source && source.FindAncestorOfType<TextBox>(includeSelf: true) is not null;
        _recognizer.Down(point, Time(e), Bounds.Width, overText);
        if (_recognizer.IsHoldPending)
        {
            _holdTimer.Stop();
            _holdTimer.Start();
        }
    }

    private void OnMoved(object? sender, PointerEventArgs e)
    {
        var result = _recognizer.Move(Position(e), Time(e));
        if (!_recognizer.IsHoldPending)
        {
            _holdTimer.Stop();
        }

        Apply(result, e.Pointer);
    }

    private void OnReleased(object? sender, PointerReleasedEventArgs e)
    {
        _holdTimer.Stop();
        Apply(_recognizer.Up(Position(e), Time(e)), pointer: null);
        e.Pointer.Capture(null);
    }

    private void OnCaptureLost(object? sender, PointerCaptureLostEventArgs e)
    {
        _holdTimer.Stop();
        Apply(_recognizer.Cancel(), pointer: null);
    }

    private void OnHoldTimer(object? sender, EventArgs e)
    {
        _holdTimer.Stop();
        Apply(_recognizer.HoldElapsed(), pointer: null);
    }

    private void Apply(GestureEvent result, IPointer? pointer)
    {
        switch (result.Kind)
        {
            case GestureEventKind.SwipeProgress:
                pointer?.Capture(this);
                SwipeProgressed?.Invoke(this, result.Dx);
                break;
            case GestureEventKind.SwipeCommitted:
                SwipeCommitted?.Invoke(this, result.Direction!.Value);
                break;
            case GestureEventKind.SwipeCancelled:
                SwipeCancelled?.Invoke(this, EventArgs.Empty);
                break;
            case GestureEventKind.HoldTriggered:
                OpenRadial(result.Point);
                break;
            case GestureEventKind.HoldMoved:
                _menu.MoveTo(_geometry.SlotAt(_origin, result.Point));
                Refresh();
                break;
            case GestureEventKind.HoldReleased:
                _menu.MoveTo(_geometry.SlotAt(_origin, result.Point));
                CloseRadial(release: true);
                break;
            case GestureEventKind.HoldCancelled:
                CloseRadial(release: false);
                break;
        }
    }

    private void OpenRadial(GesturePoint point)
    {
        _origin = point;
        _menu.Press();
        _view.Margin = new Thickness(point.X, point.Y, 0, 0);
        _view.IsVisible = true;
        Refresh();
    }

    private void CloseRadial(bool release)
    {
        var sector = _menu.Slot.SectorId;
        var outcome = release ? _menu.Release() : Cancel();
        _view.IsVisible = false;
        SetDictating(false);
        if (release)
        {
            RadialReleased?.Invoke(this, new RadialRelease(outcome, outcome == RadialOutcome.OpenSector ? sector : null));
        }
    }

    private RadialOutcome Cancel()
    {
        _menu.Cancel();
        return RadialOutcome.Cancelled;
    }

    private void Refresh()
    {
        _view.Highlight(_menu.Slot, _menu.Dictating);
        SetDictating(_menu.Dictating);
    }

    private void SetDictating(bool value)
    {
        if (_dictating == value)
        {
            return;
        }

        _dictating = value;
        DictationChanged?.Invoke(this, value);
    }

    private GesturePoint Position(PointerEventArgs e)
    {
        var p = e.GetPosition(this);
        return new GesturePoint(p.X, p.Y);
    }

    private static TimeSpan Time(PointerEventArgs e) => TimeSpan.FromMilliseconds(e.Timestamp);
}
