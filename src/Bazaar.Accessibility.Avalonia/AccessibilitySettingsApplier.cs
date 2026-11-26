using Bazaar.Accessibility;

namespace Bazaar.Accessibility.Avalonia;

/// <summary>Applies <see cref="AccessibilitySettings"/> to a window chrome and its announcement gate (doc 41c).</summary>
public static class AccessibilitySettingsApplier
{
    /// <summary>
    /// Normalises <paramref name="settings"/> and applies it: the text scale and reduced-motion flag go to the chrome, and
    /// every announcement category is configured on the gate. Safe to call again whenever the user changes a setting.
    /// </summary>
    public static void Apply(AccessibilitySettings settings, WindowChrome chrome, AnnouncementGate gate)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(chrome);
        ArgumentNullException.ThrowIfNull(gate);

        AccessibilitySettings valid = settings.Normalised();
        chrome.TextScale.SetScale(valid.TextScale);
        chrome.Motion.Reduced = valid.ReducedMotion;
        foreach (AnnouncementCategory category in Enum.GetValues<AnnouncementCategory>())
        {
            gate.Configure(category, valid.For(category));
        }
    }
}
