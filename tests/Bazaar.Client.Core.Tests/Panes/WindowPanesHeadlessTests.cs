using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.LogicalTree;
using Bazaar.Client.Core.Constellation;
using Bazaar.Client.Core.Panes;

namespace Bazaar.Client.Core.Tests.Panes;

/// <summary>
/// The Windows head's <c>--panes</c> path. Without the flag the window keeps the start content; with it the window shows
/// the Kanban and Constellation panes. The head calls <see cref="MainWindow.ShowPanes"/> with these descriptors only when
/// the flag is present, so these tests cover both windows the head can build.
/// </summary>
public class WindowPanesHeadlessTests
{
    [AvaloniaFact]
    public void Default_start_window_is_unchanged_without_panes()
    {
        var window = new MainWindow();
        window.Show();

        var start = Assert.IsType<TextBlock>(window.Content);
        Assert.Equal("Bazaar", start.Text);

        var texts = window.GetLogicalDescendants().OfType<TextBlock>().Select(text => text.Text).ToList();
        Assert.DoesNotContain("Kanban", texts);
        Assert.DoesNotContain("Constellation", texts);
        Assert.Empty(window.GetLogicalDescendants().OfType<ConstellationPaneView>());
    }

    [AvaloniaFact]
    public void Panes_flag_shows_the_kanban_and_constellation_panes_in_order()
    {
        var window = new MainWindow();
        var registry = FirstPartyPanes.CreateRegistry();

        window.ShowPanes(FirstPartyPanes.WindowPanes(), registry);
        window.Show();

        Assert.IsType<ScrollViewer>(window.Content);

        var stack = Assert.IsType<StackPanel>(((ScrollViewer)window.Content!).Content);
        Assert.Equal(2, stack.Children.Count);

        var titles = stack.Children.Select(child => Assert.IsType<StackPanel>(child).Children.OfType<TextBlock>().First().Text).ToList();
        Assert.Equal(["RyuseiFuda · Board", "Constellation"], titles);
    }

    [AvaloniaFact]
    public void Panes_flag_builds_the_constellation_view_and_replaces_the_start_content()
    {
        var window = new MainWindow();

        window.ShowPanes(FirstPartyPanes.WindowPanes(), FirstPartyPanes.CreateRegistry());
        window.Show();

        Assert.Single(window.GetLogicalDescendants().OfType<ConstellationPaneView>());
        Assert.DoesNotContain(window.GetLogicalDescendants().OfType<TextBlock>(), text => text.Text == "Bazaar");
    }
}
