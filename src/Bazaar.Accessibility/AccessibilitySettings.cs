namespace Bazaar.Accessibility;

/// <summary>
/// The viewer's accessibility choices in one value (doc 49 section 2): text scale, reduced motion and the per-category
/// live-region switches and gaps. A settings screen edits it, a head reads it and hands it to the chrome. Persisting it is
/// the settings module's job; this type only holds, defaults and validates.
/// </summary>
/// <param name="TextScale">1.0 is normal size; clamped to 1.0 through 3.0 (doc 49 asks for at least 2.0).</param>
/// <param name="ReducedMotion">Whether animation is reduced (see <see cref="MotionPolicy"/>).</param>
/// <param name="Announcements">Overrides per category; a category not listed uses <see cref="AnnouncementCategorySettings.Default"/>.</param>
public sealed record AccessibilitySettings(
    double TextScale,
    bool ReducedMotion,
    IReadOnlyDictionary<AnnouncementCategory, AnnouncementCategorySettings> Announcements)
{
    public const double MinimumTextScale = 1.0;
    public const double MaximumTextScale = 3.0;

    /// <summary>The shipped defaults: normal size, full motion, every category on with its default gap.</summary>
    public static AccessibilitySettings Default { get; } =
        new(1.0, false, new Dictionary<AnnouncementCategory, AnnouncementCategorySettings>());

    /// <summary>The settings for <paramref name="category"/>: the override if there is one, otherwise the default.</summary>
    public AnnouncementCategorySettings For(AnnouncementCategory category) =>
        Announcements.TryGetValue(category, out AnnouncementCategorySettings? settings)
            ? settings
            : AnnouncementCategorySettings.Default(category);

    /// <summary>
    /// A copy with every value made valid: the scale clamped (NaN becomes 1.0) and no announcement gap negative. Stored
    /// settings may be hand-edited or come from an older build, so a head normalises before applying.
    /// </summary>
    public AccessibilitySettings Normalised()
    {
        double scale = double.IsNaN(TextScale) ? MinimumTextScale : Math.Clamp(TextScale, MinimumTextScale, MaximumTextScale);
        var announcements = new Dictionary<AnnouncementCategory, AnnouncementCategorySettings>();
        foreach ((AnnouncementCategory category, AnnouncementCategorySettings value) in Announcements)
        {
            TimeSpan gap = value.MinimumInterval < TimeSpan.Zero ? TimeSpan.Zero : value.MinimumInterval;
            announcements[category] = value with { MinimumInterval = gap };
        }

        return new AccessibilitySettings(scale, ReducedMotion, announcements);
    }
}
