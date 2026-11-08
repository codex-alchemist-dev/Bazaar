using Avalonia.Headless.XUnit;
using Bazaar.Client.Core.Mobile;
using Bazaar.Client.Core.Panes;

namespace Bazaar.Client.Core.Tests.Mobile;

public class PhoneShellTests
{
    [AvaloniaFact]
    public void Shell_starts_folded()
    {
        var shell = new PhoneShell();

        Assert.Equal(LayoutVariant.Folded, shell.Layout);
    }

    [AvaloniaFact]
    public void Unfolding_switches_the_variant_locally()
    {
        var shell = new PhoneShell(Registry("a", "b"));

        var variant = shell.ApplyPosture(PostureKind.Unfolded, HalfOpenLayoutPolicy.UnfoldedIfWide, 900);

        Assert.Equal(LayoutVariant.Unfolded, variant);
        Assert.Equal(LayoutVariant.Unfolded, shell.Layout);
        Assert.Equal([["a", "b"]], shell.Pages.Select(p => p.ToArray()).ToArray());
    }

    [AvaloniaFact]
    public void Half_open_follows_width_under_the_default_policy()
    {
        var shell = new PhoneShell();

        Assert.Equal(LayoutVariant.Folded, shell.ApplyPosture(PostureKind.HalfOpen, HalfOpenLayoutPolicy.UnfoldedIfWide, 400));
        Assert.Equal(LayoutVariant.Unfolded, shell.ApplyPosture(PostureKind.HalfOpen, HalfOpenLayoutPolicy.UnfoldedIfWide, 700));
    }

    [AvaloniaFact]
    public void Folded_shell_pages_one_pane_each_in_registry_order()
    {
        var shell = new PhoneShell(Registry("a", "b", "c"));

        Assert.Equal([["a"], ["b"], ["c"]], shell.Pages.Select(p => p.ToArray()).ToArray());
    }

    [AvaloniaFact]
    public void Switching_to_unfolded_keeps_the_first_visible_pane_on_screen()
    {
        var shell = new PhoneShell(Registry("a", "b", "c", "d"));
        shell.GoToPage(2);
        Assert.Equal(["c"], shell.Pages[shell.SelectedPage].ToArray());

        // Folded page 2 shows pane "c". Unfolding must keep "c" on screen, which is unfolded page 1.
        shell.ApplyPosture(PostureKind.Unfolded, HalfOpenLayoutPolicy.UnfoldedIfWide, 900);

        Assert.Equal(1, shell.SelectedPage);
        Assert.Equal(["c", "d"], shell.Pages[1].ToArray());
    }

    [AvaloniaFact]
    public void Empty_registry_shows_no_pages_and_no_panes()
    {
        var shell = new PhoneShell();

        Assert.Empty(shell.Pages);
        Assert.Equal(-1, shell.SelectedPage);
    }

    private static PaneRegistry Registry(params string[] kinds)
    {
        var registry = new PaneRegistry();
        foreach (var kind in kinds)
        {
            registry.Register(kind, descriptor => new GenericPaneViewModel(descriptor));
        }

        return registry;
    }
}
