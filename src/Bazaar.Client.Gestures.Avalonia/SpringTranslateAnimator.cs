using System.Diagnostics;
using Avalonia.Controls;
using Avalonia.Media;
using Bazaar.Client.Motion;
using Bazaar.Client.Motion.Avalonia;

namespace Bazaar.Client.Gestures.Avalonia;

/// <summary>
/// Moves a control horizontally with the finger and settles it with the Fluid spring from the release velocity (doc 93, "velocity-preserving").
/// It replaces a plain translate: <see cref="Follow"/> while the finger drags, then <see cref="Cancel"/> to spring back or <see cref="Commit"/> after
/// the page has changed, so the new page springs in from where the old one left. A frame is requested only while the spring runs. At the reduced
/// motion level nothing animates: the transform is cleared at once.
/// </summary>
public sealed class SpringTranslateAnimator
{
    private readonly Control _target;
    private readonly IFrameDriver _driver;
    private readonly MotionSettings _motion;
    private readonly Func<TimeSpan> _clock;
    private readonly SpringValue _spring = new(0.1);
    private GestureSettings _settings;
    private TimeSpan? _lastTime;
    private double _lastOffset;
    private double _velocity;
    private TimeSpan? _lastFrame;
    private int _generation;

    public SpringTranslateAnimator(Control target, IFrameDriver driver, MotionSettings motion, GestureSettings? settings = null, Func<TimeSpan>? clock = null)
    {
        _target = target;
        _driver = driver;
        _motion = motion;
        _settings = (settings ?? GestureSettings.Default).Clamped();
        var watch = Stopwatch.StartNew();
        _clock = clock ?? (() => watch.Elapsed);
    }

    public bool IsAnimating => _spring.IsAnimating;

    /// <summary>The current horizontal offset in dp.</summary>
    public double Offset => _spring.Position;

    public void Configure(GestureSettings settings) => _settings = settings.Clamped();

    /// <summary>The finger is at <paramref name="dx"/>; the control follows and the release velocity is tracked.</summary>
    public void Follow(double dx)
    {
        _generation++;
        var now = _clock();
        var dt = _lastTime is { } before ? (now - before).TotalSeconds : 0;
        if (dt > 0 && dt < 0.2)
        {
            // Smoothed so one uneven pointer sample does not decide a fling.
            _velocity = 0.5 * _velocity + 0.5 * ((dx - _lastOffset) / dt);
        }
        else
        {
            _velocity = 0;
        }

        _lastTime = now;
        _lastOffset = dx;
        _spring.Set(dx);
        Apply();
    }

    /// <summary>The swipe was cancelled: spring back to rest.</summary>
    public void Cancel() => Release(_spring.Position);

    /// <summary>
    /// The swipe committed and the page has already changed. The new page starts where the old one would be (a page width away, shifted by the finger)
    /// and springs in to rest. <paramref name="direction"/> is +1 for the next page (the swipe went left) and -1 for the previous one.
    /// </summary>
    public void Commit(double pageWidth, int direction) => Release(_spring.Position + Math.Sign(direction) * Math.Max(pageWidth, 0));

    private void Release(double from)
    {
        var velocity = _velocity;
        _velocity = 0;
        _lastOffset = 0;
        _lastTime = null;
        _spring.Set(from);
        if (_motion.Effective == MotionLevel.Reduced || Math.Abs(from) < 0.01)
        {
            _spring.Set(0);
            Apply();
            return;
        }

        _spring.AnimateTo(0, _settings.SettleDamping, _settings.SettleTime, velocity);
        Apply();
        var generation = ++_generation;
        _lastFrame = null;
        _driver.RequestFrame(t => OnFrame(generation, t));
    }

    private void OnFrame(int generation, TimeSpan timestamp)
    {
        if (generation != _generation)
        {
            return;
        }

        var delta = _lastFrame is { } last ? timestamp - last : TimeSpan.Zero;
        _lastFrame = timestamp;
        var running = _spring.Advance(delta);
        Apply();
        if (running)
        {
            _driver.RequestFrame(t => OnFrame(generation, t));
        }
    }

    private void Apply() => _target.RenderTransform = Math.Abs(_spring.Position) < 0.01 && !_spring.IsAnimating ? null : new TranslateTransform(_spring.Position, 0);
}
