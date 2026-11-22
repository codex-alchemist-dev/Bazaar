namespace Bazaar.Client.Motion;

/// <summary>Chooses the effective level from the person's setting, the platform default and the OS reduced-motion flag (doc 93, settings table).</summary>
public static class MotionLevelResolver
{
    /// <summary>Liquid on phone, subtle on desktop.</summary>
    public static MotionLevel DefaultFor(MotionPlatform platform) =>
        platform == MotionPlatform.Phone ? MotionLevel.Liquid : MotionLevel.Subtle;

    /// <summary>The OS reduced-motion setting always wins; otherwise the person's choice; otherwise the platform default.</summary>
    public static MotionLevel Resolve(MotionLevel? setting, bool osReducedMotion, MotionPlatform platform) =>
        osReducedMotion ? MotionLevel.Reduced : setting ?? DefaultFor(platform);

    /// <summary>Parses the stored value of <see cref="MotionSettingKeys.MotionLevel"/> (liquid, subtle, reduced; case-insensitive).</summary>
    public static bool TryParse(string? value, out MotionLevel level)
    {
        level = default;
        return value is not null && Enum.TryParse(value.Trim(), ignoreCase: true, out level) && Enum.IsDefined(level);
    }

    /// <summary>The stored form of a level.</summary>
    public static string Format(MotionLevel level) => level switch
    {
        MotionLevel.Liquid => "liquid",
        MotionLevel.Subtle => "subtle",
        MotionLevel.Reduced => "reduced",
        _ => throw new ArgumentOutOfRangeException(nameof(level)),
    };
}
