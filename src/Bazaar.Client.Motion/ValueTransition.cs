namespace Bazaar.Client.Motion;

/// <summary>
/// One number moving to a target, shaped by the level: a spring (liquid, one wobble when the damping ratio is below 1), an ease-out
/// (subtle) or a short linear change (reduced). It starts from a value and a velocity, so a retarget in mid-flight keeps both.
/// A null variant (the preset is off) or a zero duration jumps to the target.
/// </summary>
public readonly struct ValueTransition
{
    private readonly MotionLevel _level;
    private readonly double _x0;
    private readonly double _v0;
    private readonly double _target;
    private readonly double _damping;
    private readonly double _omega;

    public ValueTransition(MotionLevel level, InteractionVariant? variant, double x0, double v0, double target)
    {
        _level = level;
        _x0 = x0;
        _v0 = v0;
        _target = target;
        TotalDuration = variant?.Duration ?? TimeSpan.Zero;
        _damping = variant?.DampingRatio ?? 1;
        _omega = TotalDuration > TimeSpan.Zero ? SpringSolver.OmegaForSettle(_damping, TotalDuration) : 0;
    }

    public TimeSpan TotalDuration { get; }

    /// <summary>Value and velocity (units per second) <paramref name="elapsed"/> after the start; exactly the target at the end.</summary>
    public (double Value, double Velocity) At(TimeSpan elapsed)
    {
        if (elapsed >= TotalDuration)
        {
            return (_target, 0);
        }

        var t = Math.Max(elapsed.TotalSeconds, 0);
        var d = TotalDuration.TotalSeconds;
        switch (_level)
        {
            case MotionLevel.Liquid:
                return SpringSolver.Evaluate(_x0, _v0, _target, _damping, _omega, t);
            case MotionLevel.Subtle:
                var inv = 1 - t / d;
                return (_target + (_x0 - _target) * inv * inv * inv, -3 * (_x0 - _target) * inv * inv / d);
            default:
                return (_x0 + (_target - _x0) * (t / d), (_target - _x0) / d);
        }
    }
}
