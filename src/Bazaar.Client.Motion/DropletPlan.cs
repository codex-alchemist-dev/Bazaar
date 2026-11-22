namespace Bazaar.Client.Motion;

/// <summary>
/// One frame of a droplet animation. Only transform and opacity: the view builds
/// <c>Translate(Tx, Ty) * Scale(Scale) * Rotate(Axis) * Scale(Along, Across) * Rotate(-Axis)</c> about the shape's centre.
/// </summary>
public readonly record struct MotionFrame(
    double Tx,
    double Ty,
    double Scale,
    double Along,
    double Across,
    double AxisDegrees,
    double Opacity,
    double ContentOpacity)
{
    /// <summary>The state at rest: no offset, full size, fully visible. A view at rest carries no transform.</summary>
    public static MotionFrame Rest { get; } = new(0, 0, 1, 1, 1, 0, 1, 1);
}

/// <summary>Which catalogue row a droplet plays (doc 93, micro-interaction catalogue).</summary>
public enum DropletKind
{
    /// <summary>"New message bubble": a message arrives from someone else, from the avatar or composer.</summary>
    Arrival,

    /// <summary>"Own message sent": the person's message rises from the composer into the list.</summary>
    OwnSent,
}

/// <summary>
/// The pure keyframes of a droplet bubble at each level, evaluated at any time without a clock.
/// Liquid: a small drop leaves the origin along a slightly bent path, stretching along its direction of travel with volume
/// kept, then expands on a spring with one overshoot; the text fades in only after the shape has settled.
/// Subtle: scale from .96 and fade (arrival) or slide 8 px and fade (own message), 160 ms. Reduced: a 90 ms fade, no travel.
/// </summary>
public sealed class DropletPlan
{
    private readonly MotionTokens _tokens;
    private readonly double _originDx;
    private readonly double _originDy;

    /// <param name="level">The effective level.</param>
    /// <param name="kind">Arrival or own message.</param>
    /// <param name="originDx">Where the drop starts, horizontally, relative to the bubble's final position (device-independent pixels).</param>
    /// <param name="originDy">Where the drop starts, vertically, relative to the bubble's final position.</param>
    /// <param name="tokens">Durations and limits; null for the defaults. Safety floors are applied.</param>
    public DropletPlan(MotionLevel level, DropletKind kind, double originDx, double originDy, MotionTokens? tokens = null)
    {
        Level = level;
        Kind = kind;
        _originDx = originDx;
        _originDy = originDy;
        _tokens = (tokens ?? MotionTokens.Default).Clamped();
        Tokens = _tokens;
        TotalDuration = level switch
        {
            MotionLevel.Liquid => _tokens.MessageArrival + _tokens.TextFadeIn,
            MotionLevel.Subtle => _tokens.SubtleDuration,
            _ => _tokens.ReducedFade,
        };
    }

    public MotionLevel Level { get; }

    public DropletKind Kind { get; }

    /// <summary>The tokens in force, after the safety floors.</summary>
    public MotionTokens Tokens { get; }

    /// <summary>Time from the first frame to <see cref="MotionFrame.Rest"/>.</summary>
    public TimeSpan TotalDuration { get; }

    /// <summary>The frame <paramref name="elapsed"/> after the start. At or past <see cref="TotalDuration"/> it is exactly <see cref="MotionFrame.Rest"/>.</summary>
    public MotionFrame Evaluate(TimeSpan elapsed)
    {
        if (elapsed >= TotalDuration)
        {
            return MotionFrame.Rest;
        }

        var t = Math.Max(elapsed.TotalSeconds, 0);
        return Level switch
        {
            MotionLevel.Liquid => Liquid(t),
            MotionLevel.Subtle => Subtle(t),
            _ => Reduced(t),
        };
    }

    private MotionFrame Reduced(double t)
    {
        var fade = Math.Clamp(t / _tokens.ReducedFade.TotalSeconds, 0, 1);
        return MotionFrame.Rest with { Opacity = fade };
    }

    private MotionFrame Subtle(double t)
    {
        var u = EaseOut(Math.Clamp(t / _tokens.SubtleDuration.TotalSeconds, 0, 1));
        return Kind == DropletKind.OwnSent
            ? MotionFrame.Rest with { Ty = _tokens.SubtleSlide * (1 - u), Opacity = u }
            : MotionFrame.Rest with { Scale = _tokens.SubtleStartScale + (1 - _tokens.SubtleStartScale) * u, Opacity = u };
    }

    private MotionFrame Liquid(double t)
    {
        var shape = _tokens.MessageArrival.TotalSeconds;
        var travel = shape * _tokens.TravelFraction;
        var start = _tokens.DropletStartScale;

        if (t < travel)
        {
            var p = t / travel;
            var ease = p * p * (3 - 2 * p);
            var distance = Math.Sqrt(_originDx * _originDx + _originDy * _originDy);

            // The path bends sideways by a share of the distance; the bend is zero at both ends.
            var bend = _tokens.DropletArc * Math.Sin(Math.PI * ease);
            var tx = _originDx * (1 - ease) + -_originDy * bend;
            var ty = _originDy * (1 - ease) + _originDx * bend;

            var stretch = distance < 1e-6 ? SquashStretchScale.Identity : SquashStretch.FromSpeed(Math.Sin(Math.PI * p), _tokens);
            var axis = distance < 1e-6 ? 0 : Math.Atan2(-_originDy, -_originDx) * 180.0 / Math.PI;

            return new MotionFrame(tx, ty, start, stretch.Along, stretch.Across, axis, Math.Clamp(p / 0.15, 0, 1), 0);
        }

        if (t < shape)
        {
            var s = SpringSolver.StepResponse(_tokens.SpringDampingRatio, TimeSpan.FromSeconds(shape - travel), t - travel);
            return MotionFrame.Rest with { Scale = start + (1 - start) * s, ContentOpacity = 0 };
        }

        return MotionFrame.Rest with { ContentOpacity = Math.Clamp((t - shape) / _tokens.TextFadeIn.TotalSeconds, 0, 1) };
    }

    private static double EaseOut(double u) => 1 - (1 - u) * (1 - u) * (1 - u);
}
