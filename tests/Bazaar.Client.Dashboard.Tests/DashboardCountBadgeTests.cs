using System.Text.Json;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Bazaar.Client.Motion;
using Bazaar.Client.Motion.Avalonia;
using Bazaar.Contracts.Panes;

namespace Bazaar.Client.Dashboard.Tests;

/// <summary>Dashboard counts show as badges that pop in and bump as the count rises.</summary>
public class DashboardCountBadgeTests
{
    private const string _model = """
        {"sections":[
          {"title":"While you were away","items":[{"label":"Unread GCs","value":0},{"label":"Mentions","value":2},{"label":"Build","value":"green"}]}
        ]}
        """;

    private static DashboardPaneViewModel Pane() =>
        new(new PaneDescriptor(PaneKinds.Dashboard, "Start", string.Empty, JsonElement.Parse(_model)));

    private static (Window Window, StackPanel Body) Show(DashboardPaneViewModel pane)
    {
        var body = DashboardPaneView.Create(pane);
        var window = new Window { Width = 300, Height = 300, Content = body };
        window.Show();
        Dispatcher.UIThread.RunJobs();
        return (window, body);
    }

    private static CountBadge Badge(StackPanel body, string label) =>
        body.GetVisualDescendants().OfType<CountBadge>().Single(b => b.Name == DashboardPaneView.BadgeName("While you were away", label));

    private static bool IsAtRest(Control c) => (c.RenderTransform is null || c.RenderTransform.Value.IsIdentity) && c.Opacity == 1;

    private static bool Plays(Func<bool> moved)
    {
        for (var i = 0; i < 20; i++)
        {
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
            Dispatcher.UIThread.RunJobs();
            if (moved()) return true;
            Thread.Sleep(10);
        }

        return false;
    }

    private static void WithLiquid(Action body)
    {
        var settings = MotionHost.Settings;
        var before = settings.Setting;
        settings.Setting = MotionLevel.Liquid;
        try
        {
            body();
        }
        finally
        {
            settings.Setting = before;
        }
    }

    [Fact]
    public void An_item_value_that_is_a_whole_number_is_a_count()
    {
        var items = Pane().Sections[0].Items;

        Assert.Equal([0, 2, null], items.Select(i => i.Count));
    }

    [Fact]
    public void Update_changes_one_figure_and_reports_it()
    {
        var pane = Pane();
        DashboardItemChanged? seen = null;
        pane.ItemChanged += (_, e) => seen = e;

        Assert.True(pane.Update("While you were away", "Mentions", "5"));
        Assert.Equal(5, pane.Sections[0].Items[1].Count);
        Assert.Equal("Mentions", seen!.Item.Label);

        Assert.False(pane.Update("While you were away", "Mentions", "5"));
        Assert.False(pane.Update("Nope", "Mentions", "6"));
        Assert.False(pane.Update("While you were away", "Nope", "6"));
    }

    [AvaloniaFact]
    public void Counts_show_as_badges_with_text_and_zero_is_hidden()
    {
        var (_, body) = Show(Pane());

        Assert.False(Badge(body, "Unread GCs").IsVisible);
        var mentions = Badge(body, "Mentions");
        Assert.True(mentions.IsVisible);
        Assert.Equal("2", mentions.Text);
        Assert.Contains(body.GetVisualDescendants().OfType<TextBlock>(), t => t.Text == "Build: green");
    }

    [AvaloniaFact]
    public void A_badge_loaded_with_a_count_does_not_animate_and_a_rise_bumps()
    {
        WithLiquid(() =>
        {
            var pane = Pane();
            var (_, body) = Show(pane);
            var mentions = Badge(body, "Mentions");
            Assert.True(IsAtRest(mentions), "the loaded count is the baseline");

            pane.Update("While you were away", "Mentions", "3");

            Assert.Equal("3", mentions.Text);
            Assert.True(Plays(() => !IsAtRest(mentions)), "the bump plays");
        });
    }

    [AvaloniaFact]
    public void A_count_that_appears_from_zero_pops_in()
    {
        WithLiquid(() =>
        {
            var pane = Pane();
            var (_, body) = Show(pane);
            var unread = Badge(body, "Unread GCs");

            pane.Update("While you were away", "Unread GCs", "1");

            Assert.True(unread.IsVisible);
            Assert.True(Plays(() => !IsAtRest(unread)), "the pop plays");
        });
    }

    [AvaloniaFact]
    public void A_pane_that_left_the_window_no_longer_follows_the_view_model()
    {
        var pane = Pane();
        var (window, body) = Show(pane);
        var mentions = Badge(body, "Mentions");
        window.Content = null;
        Dispatcher.UIThread.RunJobs();

        pane.Update("While you were away", "Mentions", "9");

        Assert.Equal("2", mentions.Text);
    }
}
