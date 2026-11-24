using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Interactivity;

namespace Bazaar.Client.PhoneCoach.Avalonia.Tests;

public class StripAndPeekHeadlessTests
{
    private static readonly string[] Names = ["General", "Kanban", "Review"];

    [AvaloniaFact]
    public void Strip_lists_every_name_with_the_current_one_highlighted()
    {
        var strip = new ChatNameStrip();
        new Window { Content = strip, Width = 360, Height = 200 }.Show();

        strip.Update(ChatStrip.From(Names, 1));

        Assert.Equal(3, strip.NameButtons.Count);
        Assert.Equal(1, strip.NameButtons[1].Opacity);
        Assert.True(strip.NameButtons[0].Opacity < 1);
        Assert.Equal(global::Avalonia.Media.FontWeight.SemiBold, ((TextBlock)strip.NameButtons[1].Content!).FontWeight);
    }

    [AvaloniaFact]
    public void Strip_updates_when_the_page_changes()
    {
        var strip = new ChatNameStrip();
        new Window { Content = strip, Width = 360, Height = 200 }.Show();
        strip.Update(ChatStrip.From(Names, 0));
        Assert.False(strip.PreviousHintVisible);
        Assert.True(strip.NextHintVisible);

        strip.Update(ChatStrip.From(Names, 2));

        Assert.Equal("Review", strip.State.Current);
        Assert.True(strip.PreviousHintVisible);
        Assert.False(strip.NextHintVisible);
        Assert.Equal(1, strip.NameButtons[2].Opacity);
        Assert.True(strip.NameButtons[0].Opacity < 1);
    }

    [AvaloniaFact]
    public void Tapping_a_name_reports_its_page()
    {
        var strip = new ChatNameStrip();
        new Window { Content = strip, Width = 360, Height = 200 }.Show();
        strip.Update(ChatStrip.From(Names, 0));
        int? tapped = null;
        strip.Selected += (_, index) => tapped = index;

        strip.NameButtons[2].RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

        Assert.Equal(2, tapped);
    }

    [AvaloniaFact]
    public void Strip_names_are_accessible()
    {
        var strip = new ChatNameStrip();
        strip.Update(ChatStrip.From(Names, 0));

        Assert.Equal(["General", "Kanban", "Review"], strip.NameButtons.Select(b => global::Avalonia.Automation.AutomationProperties.GetName(b) ?? "").ToArray());
    }

    [AvaloniaFact]
    public void Peek_edges_show_only_where_a_neighbour_exists()
    {
        var peek = new PeekEdges();

        peek.Update(ChatStrip.From(Names, 0));
        Assert.False(peek.LeftVisible);
        Assert.True(peek.RightVisible);

        peek.Update(ChatStrip.From(Names, 1));
        Assert.True(peek.LeftVisible);
        Assert.True(peek.RightVisible);

        peek.Update(ChatStrip.From(["Only"], 0));
        Assert.False(peek.LeftVisible);
        Assert.False(peek.RightVisible);
    }

    [AvaloniaFact]
    public void Peek_edges_do_not_take_touches()
    {
        Assert.False(new PeekEdges().IsHitTestVisible);
    }
}
