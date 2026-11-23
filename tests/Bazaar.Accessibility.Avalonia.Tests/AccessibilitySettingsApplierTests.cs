using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Bazaar.Localisation;

namespace Bazaar.Accessibility.Avalonia.Tests;

public class AccessibilitySettingsApplierTests
{
    [AvaloniaFact]
    public void Apply_sets_scale_motion_and_category_switches()
    {
        var gate = new AnnouncementGate(TimeProvider.System);
        var chrome = WindowChrome.Attach(new Window(), new SystemLocaleSource("en"), gate);
        var settings = new AccessibilitySettings(
            2.0,
            true,
            new Dictionary<AnnouncementCategory, AnnouncementCategorySettings>
            {
                [AnnouncementCategory.GroupMessage] = new(false, TimeSpan.Zero),
            });

        AccessibilitySettingsApplier.Apply(settings, chrome, gate);

        Assert.Equal(2.0, chrome.TextScale.Scale);
        Assert.True(chrome.Motion.Reduced);
        Assert.False(gate.Decide(AnnouncementCategory.GroupMessage, "hi").Speak);
        Assert.True(gate.Decide(AnnouncementCategory.Mention, "you").Speak);
    }

    [AvaloniaFact]
    public void Applying_the_defaults_restores_normal_settings()
    {
        var gate = new AnnouncementGate(TimeProvider.System);
        var chrome = WindowChrome.Attach(new Window(), new SystemLocaleSource("en"), gate);
        AccessibilitySettingsApplier.Apply(new AccessibilitySettings(3.0, true, new Dictionary<AnnouncementCategory, AnnouncementCategorySettings>()), chrome, gate);

        AccessibilitySettingsApplier.Apply(AccessibilitySettings.Default, chrome, gate);

        Assert.Equal(1.0, chrome.TextScale.Scale);
        Assert.False(chrome.Motion.Reduced);
    }
}
