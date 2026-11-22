using System.Globalization;
using Xunit;

namespace Bazaar.Localisation.Tests;

public class DirectionAndTimeTests
{
    [Theory]
    [InlineData("ar", TextDirection.RightToLeft)]
    [InlineData("ar-EG", TextDirection.RightToLeft)]
    [InlineData("he_IL", TextDirection.RightToLeft)]
    [InlineData("fa-IR", TextDirection.RightToLeft)]
    [InlineData("pa-Arab", TextDirection.RightToLeft)]
    [InlineData("en", TextDirection.LeftToRight)]
    [InlineData("fr-CA", TextDirection.LeftToRight)]
    [InlineData("az-Latn", TextDirection.LeftToRight)]
    [InlineData("ar-Latn", TextDirection.LeftToRight)]
    [InlineData("", TextDirection.LeftToRight)]
    [InlineData(null, TextDirection.LeftToRight)]
    public void Direction_follows_language_and_script(string? tag, TextDirection expected) =>
        Assert.Equal(expected, TextDirectionOf.ForLocale(tag));

    [Fact]
    public void Culture_direction_uses_text_info()
    {
        Assert.Equal(TextDirection.LeftToRight, TextDirectionOf.ForCulture(CultureInfo.InvariantCulture));
    }

    [Fact]
    public void Dual_time_shows_both_zones()
    {
        var instant = new DateTimeOffset(2026, 11, 19, 12, 0, 0, TimeSpan.Zero);
        TimeZoneInfo tokyo = TimeZoneInfo.CreateCustomTimeZone("t", TimeSpan.FromHours(9), "t", "t");
        TimeZoneInfo utc = TimeZoneInfo.Utc;
        DualTime t = DualTimeFormatter.Format(instant, utc, tokyo, CultureInfo.InvariantCulture);
        Assert.Contains("12:00", t.Viewer);
        Assert.Contains("21:00", t.Agent);
        Assert.False(t.SameClock);
    }

    [Fact]
    public void Dual_time_same_zone_is_same_clock()
    {
        DualTime t = DualTimeFormatter.Format(DateTimeOffset.UtcNow, TimeZoneInfo.Utc, TimeZoneInfo.Utc, CultureInfo.InvariantCulture);
        Assert.True(t.SameClock);
    }
}
