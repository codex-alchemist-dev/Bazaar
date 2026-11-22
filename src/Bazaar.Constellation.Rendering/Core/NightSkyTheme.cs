namespace Bazaar.Constellation.Rendering.Core;

/// <summary>
/// The renderer's default theme: a pitch-black void where nodes read as stars. Pure data and pure functions, so
/// every value is the same for the same input and tests need no clock or random source. Hosts may supply another
/// theme; nothing else in the core hard-codes colours.
/// </summary>
public sealed record NightSkyTheme
{
    /// <summary>The shipped default theme.</summary>
    public static NightSkyTheme Default { get; } = new();

    /// <summary>Background colour (RGBA). True black, with no gradient, grid or vignette.</summary>
    public (float R, float G, float B, float A) Background { get; init; } = (0f, 0f, 0f, 1f);

    /// <summary>Colour (RGB) of the least important stars: warm, like a red dwarf.</summary>
    public (float R, float G, float B) WarmColor { get; init; } = (1.00f, 0.62f, 0.38f);

    /// <summary>Colour (RGB) of the most important stars: cool, like a blue-white giant.</summary>
    public (float R, float G, float B) CoolColor { get; init; } = (0.66f, 0.78f, 1.00f);

    /// <summary>Radius of a star core in world units.</summary>
    public float CoreRadius { get; init; } = 0.012f;

    /// <summary>Glow radius in multiples of the core radius where the local density is zero.</summary>
    public float MinGlowScale { get; init; } = 1.5f;

    /// <summary>Glow radius in multiples of the core radius at the densest regions. Never exceeded.</summary>
    public float MaxGlowScale { get; init; } = 6f;

    /// <summary>Glow intensity (0 to 1) where the local density is zero.</summary>
    public float MinGlowIntensity { get; init; } = 0.15f;

    /// <summary>Glow intensity (0 to 1) at the densest regions. Never exceeded.</summary>
    public float MaxGlowIntensity { get; init; } = 0.85f;

    /// <summary>Density, in nodes per unit of screen area, at which the glow is halfway between its minimum and maximum.</summary>
    public float GlowHalfDensity { get; init; } = 8f;

    /// <summary>Whether twinkle is on in this theme.</summary>
    public bool TwinkleEnabled { get; init; } = true;

    /// <summary>Twinkle depth: brightness dips by at most this share (0 to 1).</summary>
    public float TwinkleDepth { get; init; } = 0.12f;

    /// <summary>Slowest twinkle period in seconds.</summary>
    public float TwinkleMaxPeriodSeconds { get; init; } = 9f;

    /// <summary>Fastest twinkle period in seconds.</summary>
    public float TwinkleMinPeriodSeconds { get; init; } = 4f;

    /// <summary>Star colour for an importance in 0 to 1 (clamped; NaN counts as 0). Higher importance is cooler.</summary>
    public (float R, float G, float B) ColorForImportance(float importance)
    {
        float t = float.IsNaN(importance) ? 0f : Math.Clamp(importance, 0f, 1f);
        return (Lerp(WarmColor.R, CoolColor.R, t), Lerp(WarmColor.G, CoolColor.G, t), Lerp(WarmColor.B, CoolColor.B, t));
    }

    /// <summary>
    /// Star colour for a cluster. The id spreads over the warm-to-cool ramp by the golden ratio, so neighbouring
    /// ids differ and the same id always gets the same colour. Negative ids are allowed.
    /// </summary>
    public (float R, float G, float B) ColorForCluster(int clusterId)
    {
        const double golden = 0.6180339887498949;
        double t = (clusterId * golden) % 1.0;
        if (t < 0)
        {
            t += 1.0;
        }

        return ColorForImportance((float)t);
    }

    /// <summary>Node style for a star: bright core in the importance colour. The glow is the density field's job.</summary>
    public NodeStyle StyleForImportance(float importance)
    {
        var (r, g, b) = ColorForImportance(importance);
        return NodeStyle.Create(r, g, b, 1f, CoreRadius);
    }

    /// <summary>Node style for a star of one cluster.</summary>
    public NodeStyle StyleForCluster(int clusterId)
    {
        var (r, g, b) = ColorForCluster(clusterId);
        return NodeStyle.Create(r, g, b, 1f, CoreRadius);
    }

    /// <summary>One style per node, from per-node importances. An empty span gives an empty table: just the void.</summary>
    public NodeStyleTable BuildStyles(ReadOnlySpan<float> importances)
    {
        var table = new NodeStyleTable(importances.Length, StyleForImportance(0f));
        for (int node = 0; node < importances.Length; node++)
        {
            table.Set(node, StyleForImportance(importances[node]));
        }

        return table;
    }

    /// <summary>
    /// Glow radius in multiples of the core radius for a local density. Rises smoothly with density and stays within
    /// <see cref="MinGlowScale"/> and <see cref="MaxGlowScale"/>. Negative or NaN density counts as zero.
    /// </summary>
    public float GlowScale(float density) => Lerp(MinGlowScale, MaxGlowScale, DensityFraction(density));

    /// <summary>Glow intensity (0 to 1) for a local density. Rises smoothly and stays within the configured bounds.</summary>
    public float GlowIntensity(float density) => Lerp(MinGlowIntensity, MaxGlowIntensity, DensityFraction(density));

    /// <summary>
    /// Brightness multiplier for one node at one time, from 1 minus <see cref="TwinkleDepth"/> up to 1. A pure function of
    /// node id and time: each node gets its own phase and period from a hash of its id. Returns 1 when twinkle is off in
    /// the theme, while the frame controller sheds detail, or with reduced motion.
    /// </summary>
    public float Twinkle(int nodeId, double timeSeconds, bool sheddingDetail = false, bool reducedMotion = false)
    {
        if (!TwinkleEnabled || sheddingDetail || reducedMotion || !double.IsFinite(timeSeconds))
        {
            return 1f;
        }

        uint h = Hash((uint)nodeId);
        double phase = (h & 0xFFFF) / 65536.0;
        double period = TwinkleMinPeriodSeconds + ((h >> 16) / 65536.0) * (TwinkleMaxPeriodSeconds - TwinkleMinPeriodSeconds);
        double wave = 0.5 + 0.5 * Math.Sin(2.0 * Math.PI * (timeSeconds / period + phase));
        float result = 1f - TwinkleDepth * (float)(1.0 - wave);
        return Math.Clamp(result, 1f - TwinkleDepth, 1f);
    }

    /// <summary>Saturating curve: 0 at zero density, 0.5 at <see cref="GlowHalfDensity"/>, approaching 1 and never reaching it.</summary>
    private float DensityFraction(float density)
    {
        if (float.IsNaN(density) || density <= 0f)
        {
            return 0f;
        }

        if (float.IsPositiveInfinity(density))
        {
            return 1f;
        }

        return density / (density + GlowHalfDensity);
    }

    private static float Lerp(float from, float to, float t) => from + (to - from) * t;

    /// <summary>Integer finaliser (murmur3 style) so neighbouring ids get unrelated phases.</summary>
    private static uint Hash(uint x)
    {
        x ^= x >> 16;
        x *= 0x85EBCA6Bu;
        x ^= x >> 13;
        x *= 0xC2B2AE35u;
        x ^= x >> 16;
        return x;
    }
}
