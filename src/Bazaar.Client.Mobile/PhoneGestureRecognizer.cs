namespace Bazaar.Client.Mobile;

/// <summary>A point in dp, relative to the gesture layer.</summary>
public readonly record struct GesturePoint(double X, double Y);

/// <summary>Which way a committed swipe moves the pager.</summary>
public enum SwipeDirection
{
    /// <summary>Finger moved right: the previous chat or pane comes in.</summary>
    Previous,

    /// <summary>Finger moved left: the next chat or pane comes in.</summary>
    Next,
}

public enum GestureEventKind
{
    /// <summary>Nothing for the head to do. The gesture is not ours, or it is still undecided.</summary>
    None,

    /// <summary>A horizontal swipe is under way. <see cref="GestureEvent.Dx"/> is the distance so far; the neighbour follows it.</summary>
    SwipeProgress,

    /// <summary>The swipe was released far or fast enough. <see cref="GestureEvent.Direction"/> says which way.</summary>
    SwipeCommitted,

    /// <summary>The swipe ended without enough distance or speed, or was interrupted. The pager springs back.</summary>
    SwipeCancelled,

    /// <summary>The press held still long enough: open the radial menu at <see cref="GestureEvent.Point"/>.</summary>
    HoldTriggered,

    /// <summary>The finger moved while the radial menu is open.</summary>
    HoldMoved,

    /// <summary>The finger lifted while the radial menu is open.</summary>
    HoldReleased,

    /// <summary>The gesture was interrupted while the radial menu is open (the system took the touch).</summary>
    HoldCancelled,
}

public readonly record struct GestureEvent(GestureEventKind Kind, double Dx = 0, SwipeDirection? Direction = null, GesturePoint Point = default)
{
    public static GestureEvent None { get; } = new(GestureEventKind.None);
}

/// <summary>
/// The phone's touch state machine (doc 93, Phone gestures): swipe against hold against scroll, with the screen edges and
/// text selection left alone. It has no UI and no clock: the head feeds it touch points with timestamps and calls
/// <see cref="HoldElapsed"/> when its hold timer fires. Edge exclusion keeps the system back gesture working.
/// </summary>
public sealed class PhoneGestureRecognizer
{
    private enum Phase
    {
        Idle,
        Pending,
        Swiping,
        Scrolling,
        Holding,
        Ignored,
    }

    private readonly GestureOptions _options;
    private Phase _phase = Phase.Idle;
    private GesturePoint _start;
    private GesturePoint _last;
    private TimeSpan _lastTime;
    private double _velocityDpPerSecond;

    public PhoneGestureRecognizer(GestureOptions? options = null)
    {
        _options = options ?? new GestureOptions();
    }

    /// <summary>True while a hold timer should run: the press is down and has not moved, swiped or been ignored.</summary>
    public bool IsHoldPending => _phase == Phase.Pending;

    /// <summary>True while the radial menu is open for this touch.</summary>
    public bool IsHolding => _phase == Phase.Holding;

    /// <summary>True while a horizontal swipe is being tracked.</summary>
    public bool IsSwiping => _phase == Phase.Swiping;

    /// <summary>A finger went down. A touch in an edge band, over selectable text or while another is active is ignored.</summary>
    public GestureEvent Down(GesturePoint point, TimeSpan time, double widthDp, bool overSelectableText = false)
    {
        if (_phase != Phase.Idle)
        {
            return GestureEvent.None;
        }

        var inEdgeBand = point.X < _options.EdgeInsetDp || point.X > widthDp - _options.EdgeInsetDp;
        _phase = inEdgeBand || overSelectableText ? Phase.Ignored : Phase.Pending;
        _start = point;
        _last = point;
        _lastTime = time;
        _velocityDpPerSecond = 0;
        return GestureEvent.None;
    }

    public GestureEvent Move(GesturePoint point, TimeSpan time)
    {
        switch (_phase)
        {
            case Phase.Pending:
                {
                    var dx = point.X - _start.X;
                    var dy = point.Y - _start.Y;
                    if (Math.Abs(dx) < _options.SlopDp && Math.Abs(dy) < _options.SlopDp)
                    {
                        return GestureEvent.None;
                    }

                    // The touch left the slop circle, so it can no longer become a hold. The dominant axis decides.
                    if (Math.Abs(dx) > Math.Abs(dy))
                    {
                        _phase = Phase.Swiping;
                        Track(point, time);
                        return new GestureEvent(GestureEventKind.SwipeProgress, dx);
                    }

                    _phase = Phase.Scrolling;
                    return GestureEvent.None;
                }

            case Phase.Swiping:
                Track(point, time);
                return new GestureEvent(GestureEventKind.SwipeProgress, point.X - _start.X);

            case Phase.Holding:
                return new GestureEvent(GestureEventKind.HoldMoved, Point: point);

            default:
                return GestureEvent.None;
        }
    }

    public GestureEvent Up(GesturePoint point, TimeSpan time)
    {
        var phase = _phase;
        var dx = point.X - _start.X;
        var velocity = _velocityDpPerSecond;
        Reset();

        return phase switch
        {
            Phase.Swiping => CommitOrCancel(dx, velocity),
            Phase.Holding => new GestureEvent(GestureEventKind.HoldReleased, Point: point),
            _ => GestureEvent.None,
        };
    }

    /// <summary>The system took the touch, or a second finger arrived. Nothing is committed.</summary>
    public GestureEvent Cancel()
    {
        var phase = _phase;
        Reset();
        return phase switch
        {
            Phase.Swiping => new GestureEvent(GestureEventKind.SwipeCancelled),
            Phase.Holding => new GestureEvent(GestureEventKind.HoldCancelled),
            _ => GestureEvent.None,
        };
    }

    /// <summary>The head's hold timer fired. Opens the radial menu only if the press is still undecided.</summary>
    public GestureEvent HoldElapsed()
    {
        if (_phase != Phase.Pending)
        {
            return GestureEvent.None;
        }

        _phase = Phase.Holding;
        return new GestureEvent(GestureEventKind.HoldTriggered, Point: _start);
    }

    private void Track(GesturePoint point, TimeSpan time)
    {
        var elapsed = (time - _lastTime).TotalSeconds;
        if (elapsed > 0)
        {
            _velocityDpPerSecond = (point.X - _last.X) / elapsed;
        }

        _last = point;
        _lastTime = time;
    }

    private GestureEvent CommitOrCancel(double dx, double velocity)
    {
        var far = Math.Abs(dx) >= _options.SwipeCommitDp;
        var flicked = Math.Abs(velocity) >= _options.SwipeCommitVelocityDpPerSecond && Math.Sign(velocity) == Math.Sign(dx);
        if (!far && !flicked)
        {
            return new GestureEvent(GestureEventKind.SwipeCancelled);
        }

        return new GestureEvent(GestureEventKind.SwipeCommitted, dx, dx < 0 ? SwipeDirection.Next : SwipeDirection.Previous);
    }

    private void Reset()
    {
        _phase = Phase.Idle;
        _velocityDpPerSecond = 0;
    }
}
