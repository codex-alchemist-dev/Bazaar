namespace Bazaar.Client.Motion;

/// <summary>
/// Plays a <see cref="DropletPlan"/> frame by frame and can be interrupted at any moment (doc 93: every animation is
/// interruptible and keeps its velocity). An interrupt does not jump: each channel carries its current value and velocity into a
/// critically damped spring to rest, so a drop that is mid-flight eases home instead of snapping. Pure: the caller supplies time.
/// </summary>
public sealed class DropletPlayer
{
    private const int _channels = 7;

    private readonly DropletPlan _plan;
    private TimeSpan _elapsed;
    private MotionFrame _current;
    private double[] _velocity = new double[_channels];

    // Settling state, set by Interrupt.
    private bool _settling;
    private double[] _from = new double[_channels];
    // The velocity at the moment of the interrupt, kept apart from the running estimate that Advance keeps updating.
    private double[] _interruptVelocity = new double[_channels];
    private double _settleSeconds;

    public DropletPlayer(DropletPlan plan)
    {
        _plan = plan;
        _current = plan.Evaluate(TimeSpan.Zero);
    }

    /// <summary>The latest frame.</summary>
    public MotionFrame Current => _current;

    /// <summary>True once the frame is <see cref="MotionFrame.Rest"/> and nothing more will change.</summary>
    public bool IsComplete { get; private set; }

    /// <summary>True while the player settles after an interrupt.</summary>
    public bool IsSettling => _settling && !IsComplete;

    /// <summary>Moves time forward and returns the new frame. Does nothing once complete.</summary>
    public MotionFrame Advance(TimeSpan delta)
    {
        if (IsComplete || delta <= TimeSpan.Zero)
        {
            return _current;
        }

        var before = ToArray(_current);
        MotionFrame next;

        if (_settling)
        {
            _settleSeconds += delta.TotalSeconds;
            next = Settle();
        }
        else
        {
            _elapsed += delta;
            next = _plan.Evaluate(_elapsed);
            if (_elapsed >= _plan.TotalDuration)
            {
                IsComplete = true;
            }
        }

        var after = ToArray(next);
        var dt = delta.TotalSeconds;
        for (var i = 0; i < _channels; i++)
        {
            _velocity[i] = (after[i] - before[i]) / dt;
        }

        _current = next;
        return _current;
    }

    /// <summary>
    /// Stops the scripted animation and eases to rest from the current values and velocities. At the reduced level there is
    /// no travel to preserve, so it ends at once.
    /// </summary>
    public void Interrupt()
    {
        if (IsComplete || _settling)
        {
            return;
        }

        if (_plan.Level == MotionLevel.Reduced)
        {
            _current = MotionFrame.Rest;
            IsComplete = true;
            return;
        }

        _settling = true;
        _from = ToArray(_current);
        _interruptVelocity = (double[])_velocity.Clone();
        _settleSeconds = 0;
    }

    private MotionFrame Settle()
    {
        var tokens = _plan.Tokens;
        if (_settleSeconds >= tokens.InterruptSettle.TotalSeconds)
        {
            IsComplete = true;
            return MotionFrame.Rest;
        }

        var rest = ToArray(MotionFrame.Rest);
        var omega = SpringSolver.OmegaForSettle(tokens.InterruptDampingRatio, tokens.InterruptSettle);
        var values = new double[_channels];
        for (var i = 0; i < _channels; i++)
        {
            values[i] = SpringSolver.Evaluate(_from[i], _interruptVelocity[i], rest[i], tokens.InterruptDampingRatio, omega, _settleSeconds).Position;
        }

        return new MotionFrame(values[0], values[1], values[2], values[3], values[4], _current.AxisDegrees,
            Math.Clamp(values[5], 0, 1), Math.Clamp(values[6], 0, 1));
    }

    private static double[] ToArray(MotionFrame f) => [f.Tx, f.Ty, f.Scale, f.Along, f.Across, f.Opacity, f.ContentOpacity];
}
