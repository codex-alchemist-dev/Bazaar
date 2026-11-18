using System.Text.Json;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Interactivity;
using Avalonia.LogicalTree;
using Avalonia.VisualTree;
using Bazaar.Client.Core.Panes;
using Bazaar.Contracts.Panes;

namespace Bazaar.Client.TimeMachine.Tests;

/// <summary>Avalonia.Headless test for the replay scrubber surface (doc 26 client UX rule 5).</summary>
public class ReplayScrubberViewHeadlessTests
{
    private static readonly Guid _gc = Guid.Parse("0196a6f0-0000-7000-8000-0000000000aa");

    private static (Window Window, Control View, ReplayScrubberViewModel Vm, InMemoryReplayClient Client) Show(
        string defaultView = "current")
    {
        var client = new InMemoryReplayClient(10, (gc, seq) => new ReplayFrameView(
            gc,
            seq,
            10,
            false,
            [new ReplayMessageView(Guid.NewGuid(), "Ada", seq, 2, true, false, $"hello at {seq}")],
            [
                ReplayPartView.Available("Kanban", "2 tasks"),
                ReplayPartView.Unavailable("Vault", "Vault is not installed"),
            ]));
        var registry = PaneRegistry.Compose([TimeMachineModule.Pane], new PaneModuleContext().With<IReplayClient>(client));
        var model = JsonElement.Parse($$"""{"gcId":"{{_gc}}","headSeq":10,"defaultView":"{{defaultView}}"}""");
        var vm = Assert.IsType<ReplayScrubberViewModel>(registry.Create(new PaneDescriptor(ReplayScrubberViewModel.PaneKind, "Replay", string.Empty, model)));
        var view = registry.CreateView(vm);
        var window = new Window { Content = view };
        window.Show();
        return (window, view, vm, client);
    }

    private static T Find<T>(Control root, string name)
        where T : Control =>
        root.GetLogicalDescendants().OfType<T>().Single(c => c.Name == name);

    private static void Click(Control root, string name) =>
        Find<Button>(root, name).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

    [AvaloniaFact]
    public void Opens_live_at_the_head_with_no_banner_and_the_frame_shown()
    {
        var (_, view, _, _) = Show();

        Assert.False(Find<TextBlock>(view, ReplayScrubberView.BannerName).GetVisualParent<Control>()!.IsVisible);
        Assert.Equal("Seq 10 of 10", Find<TextBlock>(view, ReplayScrubberView.CursorTextName).Text);
        var lines = Find<StackPanel>(view, ReplayScrubberView.MessagesName).Children.OfType<TextBlock>().Select(t => t.Text);
        Assert.Equal(["Ada: hello at 10 (edited)"], lines);
    }

    [AvaloniaFact]
    public void Step_back_shows_the_read_only_banner_with_the_viewed_seq()
    {
        var (_, view, vm, _) = Show();

        Click(view, ReplayScrubberView.BackName);

        var banner = Find<TextBlock>(view, ReplayScrubberView.BannerName);
        Assert.Equal(9, vm.CursorSeq);
        Assert.True(banner.GetVisualParent<Control>()!.IsVisible);
        Assert.Contains("seq 9 of 10", banner.Text);
        Assert.Contains("Read-only", banner.Text);
        Assert.Equal("Seq 9 of 10", Find<TextBlock>(view, ReplayScrubberView.CursorTextName).Text);
    }

    [AvaloniaFact]
    public void Slider_seeks_and_unavailable_parts_are_labelled_unavailable()
    {
        var (_, view, vm, _) = Show();

        Find<Slider>(view, ReplayScrubberView.SliderName).Value = 3;

        Assert.Equal(3, vm.CursorSeq);
        var parts = Find<StackPanel>(view, ReplayScrubberView.PartsName).Children.OfType<TextBlock>().Select(t => t.Text);
        Assert.Equal(["Kanban: 2 tasks", "Vault: unavailable (Vault is not installed)"], parts);
    }

    [AvaloniaFact]
    public void Fork_button_forks_at_the_viewed_seq_and_shows_the_notice()
    {
        var (_, view, _, client) = Show();
        Click(view, ReplayScrubberView.BackName);

        Click(view, ReplayScrubberView.ForkName);

        Assert.Equal(9, Assert.Single(client.ForkRequests).SourceSeq);
        var notice = Find<TextBlock>(view, ReplayScrubberView.NoticeName);
        Assert.True(notice.IsVisible);
        Assert.Contains("seq 9", notice.Text);
    }

    [AvaloniaFact]
    public void Mode_toggle_is_the_per_viewer_override_of_the_install_default()
    {
        var (_, view, vm, _) = Show("history");
        var toggle = Find<Button>(view, ReplayScrubberView.ModeName);
        Assert.Equal("Show current", toggle.Content);
        Assert.True(Find<TextBlock>(view, ReplayScrubberView.BannerName).GetVisualParent<Control>()!.IsVisible);

        Click(view, ReplayScrubberView.ModeName);

        Assert.Equal(ReplayViewMode.Current, vm.Mode);
        Assert.Equal("Show history", toggle.Content);
        Assert.False(Find<TextBlock>(view, ReplayScrubberView.BannerName).GetVisualParent<Control>()!.IsVisible);
    }

    [AvaloniaFact]
    public void Server_refusal_is_shown_in_the_pane()
    {
        var (_, view, _, client) = Show();
        client.Failure = new ReplayClientException("You may not read this group chat.");

        Click(view, ReplayScrubberView.BackName);

        var error = Find<TextBlock>(view, ReplayScrubberView.ErrorName);
        Assert.True(error.IsVisible);
        Assert.Equal("You may not read this group chat.", error.Text);
    }

    [AvaloniaFact]
    public void Without_a_client_the_pane_shows_its_empty_state_and_every_action_is_off()
    {
        var registry = PaneRegistry.Compose([TimeMachineModule.Pane]);
        var model = JsonElement.Parse($$"""{"gcId":"{{_gc}}","headSeq":10}""");
        var vm = registry.Create(new PaneDescriptor(ReplayScrubberViewModel.PaneKind, "Replay", string.Empty, model));
        var view = registry.CreateView(vm);
        new Window { Content = view }.Show();

        Assert.Equal("Replay is not connected to a server.", Find<TextBlock>(view, ReplayScrubberView.CursorTextName).Text);
        Assert.All(
            new[] { ReplayScrubberView.BackName, ReplayScrubberView.ForwardName, ReplayScrubberView.PlayName, ReplayScrubberView.ForkName, ReplayScrubberView.ModeName },
            name => Assert.False(Find<Button>(view, name).IsEnabled));
    }
}
