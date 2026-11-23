namespace Bazaar.Accessibility.Tests;

public class AccessibilitySettingsTests
{
    [Fact]
    public void Defaults_are_normal_size_full_motion_and_default_categories()
    {
        AccessibilitySettings settings = AccessibilitySettings.Default;

        Assert.Equal(1.0, settings.TextScale);
        Assert.False(settings.ReducedMotion);
        Assert.Equal(AnnouncementCategorySettings.Default(AnnouncementCategory.Mention), settings.For(AnnouncementCategory.Mention));
    }

    [Fact]
    public void An_override_wins_over_the_default()
    {
        var off = new AnnouncementCategorySettings(false, TimeSpan.Zero);
        var settings = AccessibilitySettings.Default with
        {
            Announcements = new Dictionary<AnnouncementCategory, AnnouncementCategorySettings> { [AnnouncementCategory.GroupMessage] = off },
        };

        Assert.Equal(off, settings.For(AnnouncementCategory.GroupMessage));
        Assert.True(settings.For(AnnouncementCategory.Mention).Enabled);
    }

    [Theory]
    [InlineData(0.2, 1.0)]
    [InlineData(2.0, 2.0)]
    [InlineData(10.0, 3.0)]
    [InlineData(double.NaN, 1.0)]
    public void Normalising_clamps_the_scale(double given, double expected) =>
        Assert.Equal(expected, (AccessibilitySettings.Default with { TextScale = given }).Normalised().TextScale);

    [Fact]
    public void Normalising_removes_negative_gaps()
    {
        var settings = AccessibilitySettings.Default with
        {
            Announcements = new Dictionary<AnnouncementCategory, AnnouncementCategorySettings>
            {
                [AnnouncementCategory.Mention] = new(true, TimeSpan.FromSeconds(-5)),
            },
        };

        Assert.Equal(TimeSpan.Zero, settings.Normalised().For(AnnouncementCategory.Mention).MinimumInterval);
    }
}
