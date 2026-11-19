using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Bazaar.Client.Views.Tests.Fixtures;
using Bazaar.Rubedo;
using Bazaar.Rubedo.Views;

namespace Bazaar.Client.Views.Tests;

/// <summary>
/// The shared pilot view documents (doc 41c, section 10.4), rendered through the real mapper. The Rubedo tests prove a pilot
/// validates; these prove it also renders: no failure card, and each condition shows what the data says.
/// </summary>
public class PilotRenderTests
{
    private static ViewDocument LoadPilot(string file) =>
        ViewDocumentReader.Read(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Golden", "pilots", file)));

    private static Dictionary<string, object?> Row(string id, string name, decimal unread, bool mentioned) =>
        ViewFixtures.Data(("id", id), ("name", name), ("unread", unread), ("mentioned", mentioned));

    private static Dictionary<string, object?> FeedData(decimal unreadGcs, decimal mentions) => ViewFixtures.Data(
        ("unreadGcs", unreadGcs),
        ("mentions", mentions),
        ("gcs", new List<IReadOnlyDictionary<string, object?>>
        {
            Row("g1", "General", 0m, false),
            Row("g2", "Releases", 4m, true),
            Row("g3", "Design", 1m, false),
        }));

    [AvaloniaFact]
    public void The_gc_feed_pilot_renders_without_a_failure_card()
    {
        var harness = new Harness();

        Control root = harness.Build(LoadPilot("gc-feed.view.json"), data: FeedData(3m, 2m));

        Assert.Empty(ViewTree.Placeholders(root));
        Assert.Contains("Unread chats", ViewTree.Texts(root));
        Assert.Contains("General", ViewTree.Texts(root));
        Assert.Contains("Releases", ViewTree.Texts(root));
    }

    [AvaloniaTheory]
    [InlineData(0, false)]
    [InlineData(2, true)]
    public void The_mentions_badge_shows_only_when_there_are_mentions(int mentions, bool expected)
    {
        var harness = new Harness();
        var stack = Assert.IsType<StackPanel>(harness.Build(LoadPilot("gc-feed.view.json"), data: FeedData(1m, mentions)));
        var header = Assert.IsType<StackPanel>(stack.Children[0]);

        Assert.Equal(expected, header.Children[2].IsVisible);
    }

    [AvaloniaFact]
    public void Each_row_shows_its_own_unread_and_mention_badges()
    {
        var harness = new Harness();
        var stack = Assert.IsType<StackPanel>(harness.Build(LoadPilot("gc-feed.view.json"), data: FeedData(3m, 2m)));
        var list = Assert.IsType<KeyedListPanel>(stack.Children[1]);

        var rows = list.Children.Select(c => Assert.IsType<StackPanel>(c)).ToList();

        Assert.Equal(3, rows.Count);
        Assert.False(rows[0].Children[1].IsVisible);
        Assert.False(rows[0].Children[2].IsVisible);
        Assert.True(rows[1].Children[1].IsVisible);
        Assert.True(rows[1].Children[2].IsVisible);
        Assert.True(rows[2].Children[1].IsVisible);
        Assert.False(rows[2].Children[2].IsVisible);
    }
}
