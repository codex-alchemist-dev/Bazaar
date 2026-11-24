using Bazaar.Client.Motion;

namespace Bazaar.Client.PhoneCoach.Avalonia;

/// <summary>
/// How the coach card enters, per motion level, from the motion tokens (doc 93: liquid on a phone, reduced as a fade of 90 ms or less).
/// Pure, so the substitution is tested without a view.
/// </summary>
/// <param name="Duration">How long the entrance takes.</param>
/// <param name="StartScale">The card's scale at the start; 1 means no scaling.</param>
public readonly record struct CoachMotionPlan(TimeSpan Duration, double StartScale)
{
    /// <summary>
    /// Reduced: a fade of <see cref="MotionTokens.ReducedFade"/>, no scale. Subtle: the subtle duration with the subtle start scale.
    /// Liquid: the longer shape time with the same start scale, so the card eases in and settles.
    /// </summary>
    public static CoachMotionPlan For(MotionLevel level, MotionTokens tokens)
    {
        ArgumentNullException.ThrowIfNull(tokens);
        return level switch
        {
            MotionLevel.Reduced => new(tokens.ReducedFade, 1),
            MotionLevel.Subtle => new(tokens.SubtleDuration, tokens.SubtleStartScale),
            _ => new(tokens.MessageArrival, tokens.SubtleStartScale),
        };
    }
}
