namespace Bazaar.Constellation.Rendering.Core;

/// <summary>
/// Colour and radius for one node. The positional constructor does not validate; use <see cref="Create"/>
/// for input that comes from outside the process.
/// </summary>
public readonly record struct NodeStyle(float Red, float Green, float Blue, float Alpha, float Radius)
{
    /// <summary>
    /// Validates and builds a style. Colour components must be finite and within 0 to 1; the radius must be
    /// finite and not negative.
    /// </summary>
    public static NodeStyle Create(float red, float green, float blue, float alpha, float radius)
    {
        ValidateUnit(red, nameof(red));
        ValidateUnit(green, nameof(green));
        ValidateUnit(blue, nameof(blue));
        ValidateUnit(alpha, nameof(alpha));
        if (!float.IsFinite(radius) || radius < 0f)
        {
            throw new ArgumentOutOfRangeException(nameof(radius), radius, "Radius must be finite and not negative.");
        }

        return new NodeStyle(red, green, blue, alpha, radius);
    }

    private static void ValidateUnit(float value, string name)
    {
        if (!float.IsFinite(value) || value < 0f || value > 1f)
        {
            throw new ArgumentOutOfRangeException(name, value, "Colour components must be finite and within 0 to 1.");
        }
    }
}
