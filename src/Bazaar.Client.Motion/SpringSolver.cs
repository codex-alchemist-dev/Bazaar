namespace Bazaar.Client.Motion;

/// <summary>
/// Closed-form damped spring (doc 93: springs are the default curve on phone). Closed form means any time can be evaluated
/// without stepping, so tests need no clock and a retarget keeps the current position and velocity exactly.
/// </summary>
public static class SpringSolver
{
    // The spring counts as settled when the remaining distance is this share of the start distance.
    private const double _settleTolerance = 0.001;

    /// <summary>Natural frequency (rad/s) at which a spring with this damping ratio settles within the tolerance in <paramref name="settle"/>.</summary>
    public static double OmegaForSettle(double dampingRatio, TimeSpan settle)
    {
        var seconds = Math.Max(settle.TotalSeconds, 1e-6);
        return Math.Log(1.0 / _settleTolerance) / (Math.Max(dampingRatio, 1e-6) * seconds);
    }

    /// <summary>Position and velocity of a spring pulled to <paramref name="target"/>, <paramref name="t"/> seconds after it started at <paramref name="x0"/> with velocity <paramref name="v0"/>.</summary>
    public static (double Position, double Velocity) Evaluate(double x0, double v0, double target, double dampingRatio, double omega, double t)
    {
        if (t <= 0)
        {
            return (x0, v0);
        }

        var d = x0 - target;
        var zeta = dampingRatio;

        if (Math.Abs(zeta - 1.0) < 1e-9)
        {
            var e = Math.Exp(-omega * t);
            var c = v0 + omega * d;
            return (target + e * (d + c * t), e * (c - omega * (d + c * t)));
        }

        if (zeta < 1.0)
        {
            var wd = omega * Math.Sqrt(1.0 - zeta * zeta);
            var e = Math.Exp(-zeta * omega * t);
            var cos = Math.Cos(wd * t);
            var sin = Math.Sin(wd * t);
            var b = (v0 + zeta * omega * d) / wd;
            var inner = d * cos + b * sin;
            var x = target + e * inner;
            var v = e * (-zeta * omega * inner + (-d * wd * sin + b * wd * cos));
            return (x, v);
        }

        var root = Math.Sqrt(zeta * zeta - 1.0);
        var r1 = omega * (-zeta + root);
        var r2 = omega * (-zeta - root);
        var c2 = (v0 - r1 * d) / (r2 - r1);
        var c1 = d - c2;
        var e1 = Math.Exp(r1 * t);
        var e2 = Math.Exp(r2 * t);
        return (target + c1 * e1 + c2 * e2, c1 * r1 * e1 + c2 * r2 * e2);
    }

    /// <summary>The step response from 0 to 1 over a settle time: reaches about 1 at <paramref name="settle"/> and overshoots once when the damping ratio is below 1.</summary>
    public static double StepResponse(double dampingRatio, TimeSpan settle, double elapsedSeconds) =>
        Evaluate(0, 0, 1, dampingRatio, OmegaForSettle(dampingRatio, settle), elapsedSeconds).Position;
}
