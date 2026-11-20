using Avalonia.Automation;
using Avalonia.Headless.XUnit;

namespace Bazaar.Accessibility.Avalonia.Tests;

public class GatedLiveRegionTests
{
    private sealed class Clock : TimeProvider
    {
        public long Ticks { get; set; }

        public override long TimestampFrequency => TimeSpan.TicksPerSecond;

        public override long GetTimestamp() => Ticks;
    }

    [AvaloniaFact]
    public void Speaks_first_then_holds_back_and_reports_the_count()
    {
        var clock = new Clock();
        var region = new GatedLiveRegion(new AnnouncementGate(clock), (text, n) => $"{text} (+{n})");

        Assert.True(region.Announce(AnnouncementCategory.GroupMessage, "one"));
        Assert.Equal("one", region.Control.Text);
        Assert.False(region.Announce(AnnouncementCategory.GroupMessage, "two"));
        Assert.Equal("one", region.Control.Text);

        clock.Ticks += TimeSpan.TicksPerSecond * 5;
        Assert.True(region.Announce(AnnouncementCategory.GroupMessage, "three"));
        Assert.Equal("three (+1)", region.Control.Text);
    }

    [AvaloniaFact]
    public void Mentions_are_assertive_and_messages_polite()
    {
        var region = new GatedLiveRegion(new AnnouncementGate(new Clock()));

        region.Announce(AnnouncementCategory.GroupMessage, "hello");
        Assert.Equal(AutomationLiveSetting.Polite, AutomationProperties.GetLiveSetting(region.Control));
        region.Announce(AnnouncementCategory.Mention, "you were mentioned");
        Assert.Equal(AutomationLiveSetting.Assertive, AutomationProperties.GetLiveSetting(region.Control));
    }

    [AvaloniaFact]
    public void A_disabled_category_stays_silent()
    {
        var gate = new AnnouncementGate(new Clock());
        gate.Configure(AnnouncementCategory.KodoIncident, new AnnouncementCategorySettings(false, TimeSpan.Zero));
        var region = new GatedLiveRegion(gate);

        Assert.False(region.Announce(AnnouncementCategory.KodoIncident, "incident"));
        Assert.Null(region.Control.Text);
    }
}
