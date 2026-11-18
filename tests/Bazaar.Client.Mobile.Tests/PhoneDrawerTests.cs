using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.VisualTree;
using Bazaar.Client.Mobile;
using Bazaar.Client.Panes.Abstractions;

namespace Bazaar.Client.Mobile.Tests;

public class PhoneDrawerTests
{
    private static readonly PhoneDrawerSpec _spec = new("constellation", "Constellation");

    private static PhoneShell Shell(PhoneDrawerSpec? drawer, Func<string, Control?>? body = null) =>
        new(new PaneRegistry(TestPaneViews.Instance), body, drawer);

    [AvaloniaFact]
    public void Shell_without_a_drawer_spec_has_none_in_any_layout()
    {
        var shell = Shell(null);
        shell.ApplyPosture(PostureKind.Unfolded, HalfOpenLayoutPolicy.UnfoldedIfWide, 900);

        Assert.Null(shell.Drawer);
        Assert.False(shell.IsDrawerShown);
    }

    [AvaloniaFact]
    public void Folded_shell_hides_the_drawer()
    {
        var shell = Shell(_spec);

        Assert.NotNull(shell.Drawer);
        Assert.False(shell.IsDrawerShown);
    }

    [AvaloniaFact]
    public void Unfolded_shell_shows_the_drawer_closed_and_read_only()
    {
        var shell = Shell(_spec);

        shell.ApplyPosture(PostureKind.Unfolded, HalfOpenLayoutPolicy.UnfoldedIfWide, 900);

        Assert.True(shell.IsDrawerShown);
        Assert.False(shell.Drawer!.IsOpen);
        Assert.True(shell.Drawer.IsReadOnly);
        Assert.Equal("constellation", shell.Drawer.Kind);
    }

    [AvaloniaFact]
    public void Drawer_body_comes_from_the_pane_body_factory_for_its_kind()
    {
        var constellation = new TextBlock { Text = "graph" };
        var shell = Shell(_spec, kind => kind == "constellation" ? constellation : null);

        Assert.Same(constellation, shell.Drawer!.Body);
    }

    [AvaloniaFact]
    public void Spec_body_wins_over_the_pane_body_factory_and_keeps_pager_bodies_separate()
    {
        var drawerBody = new TextBlock { Text = "drawer" };
        var pagerBody = new TextBlock { Text = "pager" };
        var shell = Shell(new PhoneDrawerSpec("constellation", "Constellation", () => drawerBody), _ => pagerBody);

        Assert.Same(drawerBody, shell.Drawer!.Body);
    }

    [AvaloniaFact]
    public void Drawer_without_a_real_body_shows_the_kind_name_placeholder()
    {
        var shell = Shell(_spec);

        var placeholder = Assert.IsType<TextBlock>(shell.Drawer!.Body);
        Assert.Equal("constellation", placeholder.Text);
    }

    [AvaloniaFact]
    public void Toggle_opens_and_closes_the_body()
    {
        var shell = Shell(_spec);
        shell.ApplyPosture(PostureKind.Unfolded, HalfOpenLayoutPolicy.UnfoldedIfWide, 900);
        var drawer = shell.Drawer!;

        drawer.Toggle();
        Assert.True(drawer.IsOpen);
        Assert.True(Assert.IsType<Border>(drawer.Body.Parent).IsVisible);

        drawer.Toggle();
        Assert.False(drawer.IsOpen);
        Assert.False(Assert.IsType<Border>(drawer.Body.Parent).IsVisible);
    }

    [AvaloniaFact]
    public void Clicking_the_handle_toggles_the_drawer()
    {
        var shell = Shell(_spec);
        shell.ApplyPosture(PostureKind.Unfolded, HalfOpenLayoutPolicy.UnfoldedIfWide, 900);
        var window = new Window { Content = shell, Width = 900, Height = 600 };
        window.Show();

        var handle = shell.Drawer!.GetVisualDescendants().OfType<Button>().Single();
        Assert.Equal("Constellation", handle.Content);
        handle.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));

        Assert.True(shell.Drawer!.IsOpen);
    }

    [AvaloniaFact]
    public void Open_state_survives_folding_and_unfolding()
    {
        var shell = Shell(_spec);
        shell.ApplyPosture(PostureKind.Unfolded, HalfOpenLayoutPolicy.UnfoldedIfWide, 900);
        shell.Drawer!.Open();

        shell.ApplyPosture(PostureKind.Folded, HalfOpenLayoutPolicy.UnfoldedIfWide, 400);
        Assert.False(shell.IsDrawerShown);

        shell.ApplyPosture(PostureKind.Unfolded, HalfOpenLayoutPolicy.UnfoldedIfWide, 900);
        Assert.True(shell.IsDrawerShown);
        Assert.True(shell.Drawer!.IsOpen);
    }

    [AvaloniaFact]
    public void Drawer_stays_below_the_pager_so_the_pager_takes_the_remaining_height()
    {
        var shell = Shell(_spec);
        shell.ApplyPosture(PostureKind.Unfolded, HalfOpenLayoutPolicy.UnfoldedIfWide, 900);

        var panel = Assert.IsType<DockPanel>(shell.Content);
        Assert.IsType<Carousel>(panel.Children[^1]);
        Assert.Same(shell.Drawer, panel.Children[^2]);
        Assert.Equal(Dock.Bottom, DockPanel.GetDock(shell.Drawer!));
    }
}
