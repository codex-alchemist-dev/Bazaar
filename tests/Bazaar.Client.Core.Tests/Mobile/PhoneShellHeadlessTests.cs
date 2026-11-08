using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.LogicalTree;
using Bazaar.Client.Core.Mobile;
using Bazaar.Client.Core.Panes;

namespace Bazaar.Client.Core.Tests.Mobile;

public class PhoneShellHeadlessTests
{
    [AvaloniaFact]
    public void Shell_builds_and_renders_in_folded_and_unfolded_postures()
    {
        var registry = new PaneRegistry();
        registry.Register("bazaar.first", d => new GenericPaneViewModel(d));
        registry.Register("bazaar.second", d => new GenericPaneViewModel(d));
        registry.Register("bazaar.third", d => new GenericPaneViewModel(d));
        var shell = new PhoneShell(registry);
        var window = new Window { Content = shell, Width = 360, Height = 740 };
        window.Show();

        // Folded: one pane per page, first page shows its pane name.
        Assert.Equal(LayoutVariant.Folded, shell.ApplyPosture(PostureKind.Folded, HalfOpenLayoutPolicy.UnfoldedIfWide, 360));
        Assert.Equal(3, shell.Pages.Count);
        Assert.Contains("bazaar.first", ShownText(window));
        using (var folded = window.CaptureRenderedFrame())
        {
            Assert.NotNull(folded);
        }

        // Unfolded: two panes side by side on the first page.
        window.Width = 900;
        Assert.Equal(LayoutVariant.Unfolded, shell.ApplyPosture(PostureKind.Unfolded, HalfOpenLayoutPolicy.UnfoldedIfWide, 900));
        Assert.Equal(2, shell.Pages.Count);
        Assert.Equal(["bazaar.first", "bazaar.second"], shell.Pages[0].ToArray());
        Assert.Contains("bazaar.first", ShownText(window));
        Assert.Contains("bazaar.second", ShownText(window));
        using (var unfolded = window.CaptureRenderedFrame())
        {
            Assert.NotNull(unfolded);
        }
    }

    [AvaloniaFact]
    public void Shell_with_no_panes_renders_a_placeholder()
    {
        var window = new Window { Content = new PhoneShell(), Width = 360, Height = 740 };
        window.Show();

        Assert.Contains("No panes yet", ShownText(window));
    }

    private static List<string> ShownText(Window window) =>
        window.GetLogicalDescendants().OfType<TextBlock>().Select(t => t.Text ?? string.Empty).ToList();
}
