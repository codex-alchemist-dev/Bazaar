namespace Bazaar.Client.Motion;

/// <summary>One typing dot at an instant: offset from its resting place, scale along X and Y (volume kept), and opacity.</summary>
public readonly record struct TypingDotFrame(double OffsetX, double OffsetY, double ScaleX, double ScaleY, double Opacity);

/// <summary>
/// The "Typing indicator" row of the catalogue (doc 93). Liquid: three drops that merge and separate; the outer drops lean toward
/// the middle one and stretch along X as they meet, and part again. Subtle: three dots with a small bounce, one after another.
/// Reduced: dots fade in turn, no movement. The cycle loops; the view runs it only while someone is typing.
/// </summary>
public static class TypingIndicatorPlan
{
    public const int DotCount = 3;

    /// <summary>The three dots at <paramref name="elapsed"/> into the loop. <paramref name="spacing"/> is the resting distance between neighbouring dot centres.</summary>
    public static TypingDotFrame[] Evaluate(MotionLevel level, TimeSpan elapsed, double spacing, MotionTokens? tokens = null)
    {
        var t = (tokens ?? MotionTokens.Default).Clamped();
        var cycle = t.TypingCycle.TotalSeconds;
        var phase = (elapsed.TotalSeconds % cycle + cycle) % cycle / cycle;
        var dots = new TypingDotFrame[DotCount];

        for (var i = 0; i < DotCount; i++)
        {
            dots[i] = level switch
            {
                MotionLevel.Liquid => Liquid(i, phase, spacing, t),
                MotionLevel.Subtle => Subtle(i, phase, t),
                _ => Reduced(i, phase),
            };
        }

        return dots;
    }

    private static TypingDotFrame Liquid(int index, double phase, double spacing, MotionTokens t)
    {
        // Merge amount 0 (apart) to 1 (together): a smooth bump in the middle of the cycle.
        var merge = Math.Pow(Math.Sin(Math.PI * phase), 2);
        var side = index - 1; // -1, 0, +1
        var offsetX = side * spacing * (1 - 0.85 * merge);

        // The outer drops stretch along X while they travel; the middle one swells a little as it takes them in. Volume is kept.
        var stretch = SquashStretch.FromAmount(side == 0 ? -0.5 * merge : 0.8 * merge * (1 - merge) * 4, t);
        return new TypingDotFrame(offsetX, 0, stretch.Along, stretch.Across, 1);
    }

    private static TypingDotFrame Subtle(int index, double phase, MotionTokens t)
    {
        var local = (phase - index * 0.15 + 1) % 1.0;
        var lift = local < 0.4 ? Math.Sin(Math.PI * local / 0.4) : 0;
        return new TypingDotFrame(0, -t.TypingBounce * lift, 1, 1, 1);
    }

    private static TypingDotFrame Reduced(int index, double phase)
    {
        var active = (int)Math.Min(phase * DotCount, DotCount - 1);
        return new TypingDotFrame(0, 0, 1, 1, active == index ? 1.0 : 0.35);
    }
}
