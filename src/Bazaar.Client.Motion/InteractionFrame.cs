namespace Bazaar.Client.Motion;

/// <summary>
/// One frame of a micro-interaction. Only transform, opacity and two overlay channels: <see cref="Tint"/> (opacity of a colour
/// overlay such as a hover or darken tint) and <see cref="Progress"/> (what it means is per plan, for example a knob position).
/// </summary>
public readonly record struct InteractionFrame(
    double Tx,
    double Ty,
    double Scale,
    double Along,
    double Across,
    double Opacity,
    double Tint,
    double Progress)
{
    /// <summary>No offset, full size, visible, no tint.</summary>
    public static InteractionFrame Rest { get; } = new(0, 0, 1, 1, 1, 1, 0, 0);

    /// <summary>The geometry and opacity as a <see cref="MotionFrame"/> that <c>MotionTransforms</c> can apply.</summary>
    public MotionFrame ToMotionFrame() => new(Tx, Ty, Scale, Along, Across, 0, Opacity, 1);
}

/// <summary>A plan that moves one number to a target and can be retargeted: the next plan starts from this value and velocity, so a flip in mid-flight keeps its momentum.</summary>
public interface IRetargetablePlan : IInteractionPlan
{
    /// <summary>The number and its velocity (per second) <paramref name="elapsed"/> after the start.</summary>
    (double Value, double Velocity) StateAt(TimeSpan elapsed);
}

/// <summary>A pure animation of one micro-interaction, evaluated at any time without a clock.</summary>
public interface IInteractionPlan
{
    /// <summary>Time from the first frame to the final one; zero when the interaction is instant or disabled.</summary>
    TimeSpan TotalDuration { get; }

    /// <summary>The frame <paramref name="elapsed"/> after the start. At or past <see cref="TotalDuration"/> it is the exact final frame.</summary>
    InteractionFrame Evaluate(TimeSpan elapsed);
}
