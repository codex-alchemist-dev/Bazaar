namespace Bazaar.Client.Motion;

/// <summary>Scale along and across an axis. Volume is kept: <c>Along * Across == 1</c>.</summary>
public readonly record struct SquashStretchScale(double Along, double Across)
{
    public static SquashStretchScale Identity { get; } = new(1, 1);
}

/// <summary>Squash and stretch with volume kept (doc 93, "Viscous spring": squash on one axis stretches the other).</summary>
public static class SquashStretch
{
    /// <summary>
    /// <paramref name="amount"/> in -1 to 1: 1 is the full stretch along the axis, -1 the full squash. The result is clamped to
    /// the token limits and the other axis is the reciprocal, so the area never changes.
    /// </summary>
    public static SquashStretchScale FromAmount(double amount, MotionTokens tokens)
    {
        var a = Math.Clamp(amount, -1.0, 1.0);
        var along = a >= 0
            ? 1.0 + a * (tokens.MaxStretch - 1.0)
            : 1.0 + a * (1.0 - tokens.MaxSquash);
        along = Math.Clamp(along, tokens.MaxSquash, tokens.MaxStretch);
        return new SquashStretchScale(along, 1.0 / along);
    }

    /// <summary>Stretch for a normalised speed (0 at rest, 1 at the fastest point of a move).</summary>
    public static SquashStretchScale FromSpeed(double speed01, MotionTokens tokens) =>
        FromAmount(Math.Clamp(speed01, 0.0, 1.0), tokens);
}
