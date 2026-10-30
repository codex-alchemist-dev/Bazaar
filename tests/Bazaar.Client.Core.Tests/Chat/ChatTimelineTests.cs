using Bazaar.Client.Core.Chat;
using Bazaar.Contracts.Wire;

namespace Bazaar.Client.Core.Tests.Chat;

public class ChatTimelineTests
{
    private static readonly Guid _gc = Guid.Parse("0196a6f0-0000-7000-8000-000000000001");
    private static readonly Guid _charles = Guid.Parse("0196a6f0-0000-7000-8000-0000000000aa");
    private static readonly Guid _stranger = Guid.Parse("0196a6f0-0000-7000-8000-0000000000bb");
    private static readonly DateTimeOffset _ts = new(2026, 10, 30, 9, 14, 0, TimeSpan.Zero);

    private static ChatTimeline NewTimeline() => new(
        new Dictionary<Guid, ChatMember> { [_charles] = new("Charles", IsAgent: true) },
        "Dom-tron",
        TimeZoneInfo.Utc);

    private static WireGcMessage Msg(long n, long ev, Guid author, string text) =>
        new(_gc, n, ev, Guid.NewGuid(), author, text, _ts.AddMinutes(n), ev > 1, [], null);

    [Fact]
    public void Lines_come_out_in_sequence_order()
    {
        var timeline = NewTimeline();
        timeline.Apply(Msg(2, 1, _charles, "second"));
        timeline.Apply(Msg(1, 1, _charles, "first"));

        Assert.Equal(["first", "second"], timeline.Lines().Select(l => l.Text));
    }

    [Fact]
    public void A_newer_version_replaces_the_older_one_and_an_older_one_is_ignored()
    {
        var timeline = NewTimeline();
        Assert.True(timeline.Apply(Msg(1, 1, _charles, "draft")));
        Assert.True(timeline.Apply(Msg(1, 2, _charles, "final")));
        Assert.False(timeline.Apply(Msg(1, 1, _charles, "stale")));

        var line = Assert.Single(timeline.Lines());
        Assert.Equal("final", line.Text);
        Assert.True(line.Edited);
        Assert.Contains("(edited)", line.Header, StringComparison.Ordinal);
    }

    [Fact]
    public void Agents_carry_the_marker_and_unknown_authors_show_a_short_id()
    {
        var timeline = NewTimeline();
        timeline.Apply(Msg(1, 1, _charles, "hi"));
        timeline.Apply(Msg(2, 1, _stranger, "who"));

        var lines = timeline.Lines();
        Assert.Equal("Charles [agent]", lines[0].Author);
        Assert.Equal("[09:15] Charles [agent]", lines[0].Header);
        Assert.Equal(_stranger.ToString("N")[..8], lines[1].Author);
    }

    [Fact]
    public void Messages_this_client_sent_show_its_own_label()
    {
        var timeline = NewTimeline();
        timeline.Apply(Msg(5, 1, _stranger, "mine"));
        timeline.MarkOwn(5);

        var line = Assert.Single(timeline.Lines());
        Assert.True(line.Own);
        Assert.Equal("Dom-tron", line.Author);
    }

    [Fact]
    public void Clock_time_uses_the_given_zone()
    {
        var timeline = new ChatTimeline(new Dictionary<Guid, ChatMember>(), "me", TimeZoneInfo.CreateCustomTimeZone("plus2", TimeSpan.FromHours(2), "plus2", "plus2"));
        timeline.Apply(Msg(1, 1, _stranger, "x"));

        Assert.Equal("11:15", timeline.Lines().Single().Time);
    }
}
