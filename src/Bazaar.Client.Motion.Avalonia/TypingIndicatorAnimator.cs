using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace Bazaar.Client.Motion.Avalonia;

/// <summary>
/// Drives the three typing dots (doc 93 catalogue row "Typing indicator"). It loops only between <see cref="Start"/> and
/// <see cref="Stop"/>, so there is no work at rest. The level is read on every frame, so a level switch takes effect at once.
/// </summary>
public sealed class TypingIndicatorAnimator
{
    private readonly IReadOnlyList<Control> _dots;
    private readonly MotionSettings _settings;
    private readonly IFrameDriver _driver;
    private readonly double _spacing;

    private TimeSpan _elapsed;
    private TimeSpan? _lastTimestamp;
    private int _generation;

    /// <param name="dots">Exactly <see cref="TypingIndicatorPlan.DotCount"/> controls.</param>
    /// <param name="spacing">Resting distance between neighbouring dot centres.</param>
    public TypingIndicatorAnimator(IReadOnlyList<Control> dots, MotionSettings settings, IFrameDriver driver, double spacing)
    {
        ArgumentOutOfRangeException.ThrowIfNotEqual(dots.Count, TypingIndicatorPlan.DotCount);
        _dots = dots;
        _settings = settings;
        _driver = driver;
        _spacing = spacing;
    }

    public bool IsRunning { get; private set; }

    public void Start()
    {
        if (IsRunning)
        {
            return;
        }

        IsRunning = true;
        _lastTimestamp = null;
        _generation++;
        Advance(TimeSpan.Zero);
        var generation = _generation;
        _driver.RequestFrame(ts => Tick(ts, generation));
    }

    public void Stop()
    {
        IsRunning = false;
        _generation++;
        foreach (var dot in _dots)
        {
            dot.RenderTransform = null;
            dot.Opacity = 1;
        }
    }

    /// <summary>Moves the loop forward by hand and applies the frame. Public so tests need no clock.</summary>
    public void Advance(TimeSpan delta)
    {
        _elapsed += delta;
        var frames = TypingIndicatorPlan.Evaluate(_settings.Effective, _elapsed, _spacing, _settings.Tokens);
        for (var i = 0; i < frames.Length; i++)
        {
            var f = frames[i];
            var dot = _dots[i];
            dot.RenderTransformOrigin = RelativePoint.Center;
            dot.RenderTransform = new MatrixTransform(Matrix.CreateScale(f.ScaleX, f.ScaleY) * Matrix.CreateTranslation(f.OffsetX, f.OffsetY));
            dot.Opacity = f.Opacity;
        }
    }

    private void Tick(TimeSpan timestamp, int generation)
    {
        if (generation != _generation || !IsRunning)
        {
            return;
        }

        var delta = _lastTimestamp is { } last ? timestamp - last : TimeSpan.Zero;
        _lastTimestamp = timestamp;
        Advance(delta);
        _driver.RequestFrame(ts => Tick(ts, generation));
    }
}
