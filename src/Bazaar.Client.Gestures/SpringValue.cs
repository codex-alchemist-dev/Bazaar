using Bazaar.Client.Motion;

namespace Bazaar.Client.Gestures;

/// <summary>
/// One number that follows the finger while it is down and springs to a target when released, using the closed-form <see cref="SpringSolver"/>.
/// Retargeting (a release, or a new touch during a settle) starts from the current position and velocity, so an interrupted
/// animation never jumps (doc 93: "velocity-preserving"). Time comes in as deltas; nothing here reads a clock.
/// </summary>
public sealed class SpringValue
{
    private readonly double _tolerance;
    private double _start;
    private double _startVelocity;
    private double _elapsed;
    private double _damping = 1;
    private double _omega = 1;

    /// <param name="tolerance">Distance and speed (in the value's own unit) under which the spring counts as at rest.</param>
    public SpringValue(double tolerance = 0.01) => _tolerance = tolerance;

    public double Position { get; private set; }

    public double Velocity { get; private set; }

    public double Target { get; private set; }

    /// <summary>True while a spring is running toward <see cref="Target"/>.</summary>
    public bool IsAnimating { get; private set; }

    /// <summary>Places the value directly (the finger is moving it). Stops any spring and keeps no velocity.</summary>
    public void Set(double position)
    {
        Position = position;
        Velocity = 0;
        Target = position;
        IsAnimating = false;
    }

    /// <summary>Shifts the value and its spring by <paramref name="delta"/> without disturbing the motion (a change of reference frame).</summary>
    public void Rebase(double delta)
    {
        Position += delta;
        Target += delta;
        _start += delta;
    }

    /// <summary>Springs to <paramref name="target"/> from the current position, with <paramref name="velocity"/> (the release velocity) or, when null, the current one.</summary>
    public void AnimateTo(double target, double dampingRatio, TimeSpan settle, double? velocity = null)
    {
        _start = Position;
        _startVelocity = velocity ?? Velocity;
        Velocity = _startVelocity;
        _damping = dampingRatio;
        _omega = SpringSolver.OmegaForSettle(dampingRatio, settle);
        _elapsed = 0;
        Target = target;
        IsAnimating = true;
        if (AtRest(Position, Velocity))
        {
            Finish();
        }
    }

    /// <summary>Jumps to the target now (reduced motion).</summary>
    public void Snap(double target) => Set(target);

    /// <summary>Moves the spring on by <paramref name="delta"/>. Returns true while it is still running.</summary>
    public bool Advance(TimeSpan delta)
    {
        if (!IsAnimating)
        {
            return false;
        }

        _elapsed += Math.Max(delta.TotalSeconds, 0);
        var (position, velocity) = SpringSolver.Evaluate(_start, _startVelocity, Target, _damping, _omega, _elapsed);
        Position = position;
        Velocity = velocity;
        if (AtRest(position, velocity))
        {
            Finish();
        }

        return IsAnimating;
    }

    private bool AtRest(double position, double velocity) =>
        Math.Abs(position - Target) <= _tolerance && Math.Abs(velocity) <= _tolerance * 20;

    private void Finish()
    {
        Position = Target;
        Velocity = 0;
        IsAnimating = false;
    }
}
