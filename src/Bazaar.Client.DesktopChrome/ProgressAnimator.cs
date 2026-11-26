using Bazaar.Client.Motion;
using Bazaar.Client.Motion.Avalonia;

namespace Bazaar.Client.DesktopChrome;

/// <summary>
/// Animates one value toward a target, 0 to 1 by convention, and tells its owner after every step. It is interruptible: retargeting
/// keeps the current value and, on the spring curve, the current velocity. The spring is critically damped (damping ratio 1), so it
/// never overshoots, which is the desktop rule. A frame is requested only while it runs, so a settled control costs nothing (doc 93).
/// </summary>
public sealed class ProgressAnimator
{
    private const double _criticalDamping = 1.0;

    private readonly IFrameDriver _frames;

    private double _from;
    private double _velocity0;
    private double _omega;
    private TimeSpan _duration;
    private bool _spring;
    private TimeSpan? _start;
    private int _generation;

    public ProgressAnimator(IFrameDriver frames, double initial = 0)
    {
        ArgumentNullException.ThrowIfNull(frames);
        _frames = frames;
        Value = initial;
        Target = initial;
    }

    public double Value { get; private set; }

    public double Velocity { get; private set; }

    public double Target { get; private set; }

    public bool IsRunning { get; private set; }

    /// <summary>Raised after the value changes.</summary>
    public event EventHandler? Changed;

    /// <summary>Raised when the value has reached its target.</summary>
    public event EventHandler? Completed;

    /// <summary>Jumps to <paramref name="value"/> and stops.</summary>
    public void Snap(double value)
    {
        _generation++;
        IsRunning = false;
        Target = value;
        Value = value;
        Velocity = 0;
        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Starts moving to <paramref name="target"/> from the current value; a run in progress is interrupted.</summary>
    /// <param name="spring">True for the critically damped spring, false for a linear fade.</param>
    public void AnimateTo(double target, TimeSpan duration, bool spring)
    {
        if (duration <= TimeSpan.Zero || Value == target)
        {
            Snap(target);
            Completed?.Invoke(this, EventArgs.Empty);
            return;
        }

        _from = Value;
        _velocity0 = spring ? Velocity : 0;
        Target = target;
        _duration = duration;
        _spring = spring;
        _omega = SpringSolver.OmegaForSettle(_criticalDamping, duration);
        _start = null;
        IsRunning = true;
        int generation = ++_generation;
        _frames.RequestFrame(now => OnFrame(now, generation));
    }

    /// <summary>Moves the animation to <paramref name="now"/>; frames from the driver call this, and tests call it by hand.</summary>
    public void Advance(TimeSpan now)
    {
        if (!IsRunning)
        {
            return;
        }

        _start ??= now;
        TimeSpan elapsed = now - _start.Value;
        if (elapsed >= _duration)
        {
            IsRunning = false;
            Value = Target;
            Velocity = 0;
            Changed?.Invoke(this, EventArgs.Empty);
            Completed?.Invoke(this, EventArgs.Empty);
            return;
        }

        if (_spring)
        {
            (Value, Velocity) = SpringSolver.Evaluate(_from, _velocity0, Target, _criticalDamping, _omega, elapsed.TotalSeconds);
        }
        else
        {
            Value = _from + ((Target - _from) * (elapsed / _duration));
            Velocity = 0;
        }

        Changed?.Invoke(this, EventArgs.Empty);
    }

    private void OnFrame(TimeSpan now, int generation)
    {
        if (generation != _generation)
        {
            return;
        }

        Advance(now);
        if (IsRunning)
        {
            _frames.RequestFrame(next => OnFrame(next, generation));
        }
    }
}
