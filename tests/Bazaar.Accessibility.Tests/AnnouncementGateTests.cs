using Bazaar.Accessibility;
using Microsoft.Extensions.Time.Testing;

namespace Bazaar.Accessibility.Tests;

public class AnnouncementGateTests
{
    private readonly FakeTimeProvider _time = new();

    [Fact]
    public void The_first_announcement_speaks_at_once()
    {
        var gate = new AnnouncementGate(_time);

        AnnouncementDecision d = gate.Decide(AnnouncementCategory.GroupMessage, "Ada: hello");

        Assert.True(d.Speak);
        Assert.Equal(0, d.SuppressedBefore);
    }

    [Fact]
    public void Announcements_inside_the_interval_are_held_back_and_counted()
    {
        var gate = new AnnouncementGate(_time);
        gate.Decide(AnnouncementCategory.GroupMessage, "one");

        _time.Advance(TimeSpan.FromSeconds(1));
        Assert.False(gate.Decide(AnnouncementCategory.GroupMessage, "two").Speak);
        _time.Advance(TimeSpan.FromSeconds(1));
        Assert.False(gate.Decide(AnnouncementCategory.GroupMessage, "three").Speak);

        _time.Advance(TimeSpan.FromSeconds(3));
        AnnouncementDecision d = gate.Decide(AnnouncementCategory.GroupMessage, "four");
        Assert.True(d.Speak);
        Assert.Equal(2, d.SuppressedBefore);
        Assert.Equal("four", d.Text);
    }

    [Fact]
    public void The_count_resets_after_a_spoken_announcement()
    {
        var gate = new AnnouncementGate(_time);
        gate.Decide(AnnouncementCategory.GroupMessage, "one");
        gate.Decide(AnnouncementCategory.GroupMessage, "two");
        _time.Advance(TimeSpan.FromSeconds(5));
        gate.Decide(AnnouncementCategory.GroupMessage, "three");

        _time.Advance(TimeSpan.FromSeconds(5));
        Assert.Equal(0, gate.Decide(AnnouncementCategory.GroupMessage, "four").SuppressedBefore);
    }

    [Fact]
    public void Categories_are_independent()
    {
        var gate = new AnnouncementGate(_time);
        gate.Decide(AnnouncementCategory.GroupMessage, "chat");

        Assert.True(gate.Decide(AnnouncementCategory.Mention, "@you").Speak);
        Assert.True(gate.Decide(AnnouncementCategory.KodoIncident, "incident").Speak);
    }

    [Fact]
    public void A_disabled_category_never_speaks()
    {
        var gate = new AnnouncementGate(_time);
        gate.Configure(AnnouncementCategory.GroupMessage, new AnnouncementCategorySettings(false, TimeSpan.Zero));

        Assert.False(gate.Decide(AnnouncementCategory.GroupMessage, "chat").Speak);
        _time.Advance(TimeSpan.FromHours(1));
        Assert.False(gate.Decide(AnnouncementCategory.GroupMessage, "chat").Speak);
        Assert.True(gate.Decide(AnnouncementCategory.Mention, "@you").Speak);
    }

    [Fact]
    public void A_configured_interval_replaces_the_default()
    {
        var gate = new AnnouncementGate(_time);
        gate.Configure(AnnouncementCategory.GroupMessage, new AnnouncementCategorySettings(true, TimeSpan.FromSeconds(30)));
        gate.Decide(AnnouncementCategory.GroupMessage, "one");

        _time.Advance(TimeSpan.FromSeconds(10));
        Assert.False(gate.Decide(AnnouncementCategory.GroupMessage, "two").Speak);
        _time.Advance(TimeSpan.FromSeconds(20));
        Assert.True(gate.Decide(AnnouncementCategory.GroupMessage, "three").Speak);
    }

    [Fact]
    public void A_zero_interval_speaks_every_time()
    {
        var gate = new AnnouncementGate(_time);
        gate.Configure(AnnouncementCategory.ApprovalRequest, new AnnouncementCategorySettings(true, TimeSpan.Zero));

        Assert.True(gate.Decide(AnnouncementCategory.ApprovalRequest, "a").Speak);
        Assert.True(gate.Decide(AnnouncementCategory.ApprovalRequest, "b").Speak);
    }

    [Fact]
    public void Bad_input_is_rejected()
    {
        var gate = new AnnouncementGate(_time);

        Assert.Throws<ArgumentException>(() => gate.Decide(AnnouncementCategory.Mention, " "));
        Assert.Throws<ArgumentOutOfRangeException>(() => gate.Configure(AnnouncementCategory.Mention, new AnnouncementCategorySettings(true, TimeSpan.FromSeconds(-1))));
    }
}
