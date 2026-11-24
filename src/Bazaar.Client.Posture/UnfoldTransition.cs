namespace Bazaar.Client.Posture;

/// <summary>What drives the progress right now.</summary>
public enum UnfoldMode
{
    /// <summary>At rest; nothing is requested.</summary>
    Idle,

    /// <summary>Following the fold angle sample by sample.</summary>
    Tracking,

    /// <summary>A timed spring to the target, played on a posture change (no angle available, or the hands let go).</summary>
    Timed,

    /// <summary>Reduced motion: a cross-fade of at most 90 ms, no travel.</summary>
    Fading,
}

/// <summary>
/// One frame of the unfold transition (doc 93). <see cref="Progress"/> is 0 folded and 1 settled in the chosen layout (a liquid
/// spring may pass 1 slightly). Only opacity and translation are meant to be animated from the other channels.
/// </summary>
public readonly record struct UnfoldFrame(
    double Progress,
    double ContentOpacity,
    double NewPaneOpacity,
    double NewPaneOffset,
    double ChatStripOpacity,
    double PagerOpacity)
{
    /// <summary>The frame at rest in the folded layout.</summary>
    public static UnfoldFrame Folded { get; } = FromProgress(0);

    /// <summary>
    /// The channels for a progress: the new pane slides (1 to 0, a share of its extent from the hinge side) and fades in over
    /// the middle of the unfold; the chat-name strip eases out as the pane pager eases in.
    /// </summary>
    public static UnfoldFrame FromProgress(double progress, double contentOpacity = 1)
    {
        var p = Math.Clamp(progress, 0, 1);
        return new UnfoldFrame(
            progress,
            contentOpacity,
            Math.Clamp((p - 0.2) / 0.6, 0, 1),
            1 - p,
            1 - Math.Clamp(p / 0.5, 0, 1),
            Math.Clamp((p - 0.4) / 0.5, 0, 1));
    }
}

/// <summary>
/// The unfold and fold transition, pure: the caller supplies time and angles. Where the platform reports the fold angle the
/// progress tracks it (follows the hands); when no angle comes, or when the hands let go, a timed spring (<see cref="Bazaar.Client.Motion.SpringSolver"/>)
/// carries it to the settled layout. Folding is the same transition toward 0. Retargeting mid-flight keeps position and velocity.
/// Reduced motion plays a cross-fade of at most 90 ms (fade out, switch layout, fade in) and ignores the angle.
/// </summary>
public sealed class UnfoldTransition
{
    private readonly PostureMotionTokens _tokens;
    private readonly Motion.MotionTokens _motion;
    private Motion.MotionLevel _level;

    private UnfoldMode _mode = UnfoldMode.Idle;
    private double _progress;
    private double _velocity;
    private double _goal;

    private double _x0;
    private double _v0;
    private double _t;
    private double _omega;
    private double _zeta;
    private double _duration;
    private double _timedTarget;
    private double _sinceSample;

    private double _fadeFrom;
    private double _fadeTarget;
    private double _contentOpacity = 1;

    public UnfoldTransition(Motion.MotionLevel level, PostureMotionTokens? tokens = null, Motion.MotionTokens? motion = null, double initialProgress = 0)
    {
        _level = level;
        _tokens = (tokens ?? PostureMotionTokens.Default).Clamped();
        _motion = (motion ?? Motion.MotionTokens.Default).Clamped();
        _progress = initialProgress;
        _goal = initialProgress;
    }

    public UnfoldMode Mode => _mode;

    public bool IsSettled => _mode == UnfoldMode.Idle;

    /// <summary>The layout the transition is heading for (0 folded, 1 settled in the chosen layout).</summary>
    public double Goal => _goal;

    public double Progress => _progress;

    public UnfoldFrame Current
    {
        get
        {
            var frame = UnfoldFrame.FromProgress(_progress, _contentOpacity);
            // Reduced motion has no travel: the pane never slides, it only cross-fades with the layout.
            return _level == Motion.MotionLevel.Reduced ? frame with { NewPaneOffset = 0 } : frame;
        }
    }

    /// <summary>Jumps to a progress with no motion (first reading after start).</summary>
    public void Snap(double progress)
    {
        _mode = UnfoldMode.Idle;
        _progress = progress;
        _goal = progress;
        _velocity = 0;
        _contentOpacity = 1;
    }

    /// <summary>Changes the level. Whatever is in flight stops at once and the layout rests at its goal.</summary>
    public void SetLevel(Motion.MotionLevel level)
    {
        if (level == _level)
        {
            return;
        }

        _level = level;
        Snap(_goal);
    }

    /// <summary>
    /// Sets the layout the transition should settle in. While tracking the hinge the goal is only remembered and used when the hands let go;
    /// otherwise a timed spring (or the cross-fade at the reduced level) starts toward it.
    /// </summary>
    public void Retarget(double target)
    {
        _goal = target;
        switch (_mode)
        {
            case UnfoldMode.Tracking:
                return;
            case UnfoldMode.Timed when _timedTarget == target:
                return;
            case UnfoldMode.Fading when _fadeTarget == target:
                return;
            case UnfoldMode.Idle when _progress == target:
                return;
        }

        Start(target);
    }

    /// <summary>Follows the fold angle (0 closed, 180 flat). Ignored at the reduced level, where Retarget plays the cross-fade.</summary>
    public void TrackAngle(double angleDegrees)
    {
        if (_level == Motion.MotionLevel.Reduced)
        {
            return;
        }

        var span = _tokens.OpenDegrees - _tokens.ClosedDegrees;
        _progress = Math.Clamp((angleDegrees - _tokens.ClosedDegrees) / span, 0, 1);
        _velocity = 0;
        _sinceSample = 0;
        _mode = UnfoldMode.Tracking;
        _contentOpacity = 1;
    }

    /// <summary>Moves time forward and returns the frame.</summary>
    public UnfoldFrame Advance(TimeSpan delta)
    {
        var dt = Math.Max(delta.TotalSeconds, 0);
        switch (_mode)
        {
            case UnfoldMode.Tracking:
                _sinceSample += dt;
                if (_sinceSample >= _tokens.TrackIdle.TotalSeconds)
                {
                    _mode = UnfoldMode.Idle;
                    if (_progress == _goal)
                    {
                        _velocity = 0;
                    }
                    else
                    {
                        Start(_goal);
                    }
                }

                break;

            case UnfoldMode.Timed:
                _t += dt;
                if (_t >= _duration)
                {
                    _progress = _timedTarget;
                    _velocity = 0;
                    _mode = UnfoldMode.Idle;
                }
                else
                {
                    (_progress, _velocity) = Motion.SpringSolver.Evaluate(_x0, _v0, _timedTarget, _zeta, _omega, _t);
                }

                break;

            case UnfoldMode.Fading:
                AdvanceFade(dt);
                break;
        }

        return Current;
    }

    private void Start(double target)
    {
        if (_level == Motion.MotionLevel.Reduced)
        {
            _fadeFrom = _progress;
            _fadeTarget = target;
            _t = 0;
            _mode = UnfoldMode.Fading;
            _velocity = 0;
            AdvanceFade(0);
            return;
        }

        var liquid = _level == Motion.MotionLevel.Liquid;
        _x0 = _progress;
        _v0 = _velocity;
        _t = 0;
        _zeta = liquid ? _motion.SpringDampingRatio : 1.0;
        _duration = _tokens.UnfoldDuration.TotalSeconds * (liquid ? 1.0 : _tokens.SubtleScale);
        _omega = Motion.SpringSolver.OmegaForSettle(_zeta, TimeSpan.FromSeconds(_duration));
        _timedTarget = target;
        _mode = UnfoldMode.Timed;
    }

    private void AdvanceFade(double dt)
    {
        _t += dt;
        var total = _motion.ReducedFade.TotalSeconds;
        if (total <= 0 || _t >= total)
        {
            _progress = _fadeTarget;
            _contentOpacity = 1;
            _mode = UnfoldMode.Idle;
            return;
        }

        var half = total / 2;
        if (_t < half)
        {
            _progress = _fadeFrom;
            _contentOpacity = 1 - _t / half;
        }
        else
        {
            _progress = _fadeTarget;
            _contentOpacity = (_t - half) / half;
        }
    }
}
