namespace Bazaar.Constellation.Rendering.Core;

/// <summary>
/// Colour, radius and shape for one node. <see cref="RingWidth"/> is the share of the radius that a ring keeps:
/// 0 draws a filled disc, and 1 draws a full disc too. Between 0 and 1 the centre is cut out, leaving a band of that
/// width at the rim. The positional constructor does not validate; use <see cref="Create"/> for input that comes
/// from outside the process.
/// </summary>
public readonly record struct NodeStyle(float Red, float Green, float Blue, float Alpha, float Radius, float RingWidth)
{
    /// <summary>
    /// Validates and builds a style. Colour components must be finite and within 0 to 1; the radius must be
    /// finite and not negative; the ring width must be finite and within 0 to 1.
    /// </summary>
    public static NodeStyle Create(float red, float green, float blue, float alpha, float radius, float ringWidth = 0f)
    {
        ValidateUnit(red, nameof(red));
        ValidateUnit(green, nameof(green));
        ValidateUnit(blue, nameof(blue));
        ValidateUnit(alpha, nameof(alpha));
        if (!float.IsFinite(radius) || radius < 0f)
        {
            throw new ArgumentOutOfRangeException(nameof(radius), radius, "Radius must be finite and not negative.");
        }

        ValidateUnit(ringWidth, nameof(ringWidth));
        return new NodeStyle(red, green, blue, alpha, radius, ringWidth);
    }

    private static void ValidateUnit(float value, string name)
    {
        if (!float.IsFinite(value) || value < 0f || value > 1f)
        {
            throw new ArgumentOutOfRangeException(name, value, "Value must be finite and within 0 to 1.");
        }
    }
}
