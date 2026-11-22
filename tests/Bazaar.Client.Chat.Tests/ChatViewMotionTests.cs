using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Bazaar.Client.Abstractions.Wire;
using Bazaar.Client.Chat.Abstractions;
using Bazaar.Client.Motion;
using Bazaar.Client.Motion.Avalonia;
using Bazaar.Contracts.Wire;
using Bazaar.Testing;

namespace Bazaar.Client.Chat.Tests;

/// <summary>The chat view plays the droplet only for messages that arrive while it is shown, and the send button follows the draft.</summary>
public class ChatViewMotionTests
{
    private static readonly Guid _gc = Guid.Parse("0196a6f0-0000-7000-8000-000000000001");

    private static WireGcMessage Message(long n, string text) =>
        new(_gc, n, 1, Guid.NewGuid(), Guid.Empty, text, DateTimeOffset.UnixEpoch, false, [], null);

    private static (ChatViewModel Vm, ChatTimeline Timeline) Build(int initial)
    {
        var transport = new FakeWireTransport();
        var timeline = new ChatTimeline(new Dictionary<Guid, ChatMember> { [Guid.Empty] = new("Charles", IsAgent: true) }, "me", TimeZoneInfo.Utc);
        for (var i = 1; i <= initial; i++)
        {
            timeline.Apply(Message(i, $"line {i}"));
        }

        var client = new WireClient(transport, transport.Codec, new InMemoryCursorStore(),
            new WireClientOptions { DeviceId = "linux-test", DeviceClass = "linux", ClientBuild = "test" }, new FakeClock());
        var session = new GcChatSession(client, new ChatConnectionSettings(new Uri("ws://h/ws"), "t", _gc, "me"), timeline);
        return (new ChatViewModel(session), timeline);
    }

    private static (Window Window, ListBox Lines) Show(ChatViewModel vm)
    {
        var window = new Window { Width = 400, Height = 400, Content = ChatViewFactory.Instance.Create(vm) };
        window.Show();
        Dispatcher.UIThread.RunJobs();
        return (window, window.GetVisualDescendants().OfType<ListBox>().Single());
    }

    private static Control Line(ListBox lines, int index) => (Control)lines.ContainerFromIndex(index)!;

    private static bool IsAtRest(Control c) => (c.RenderTransform is null || c.RenderTransform.Value.IsIdentity) && c.Opacity == 1;

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

    [AvaloniaFact]
    public void An_initial_load_does_not_animate_and_a_new_message_does()
    {
        WithLiquid(() =>
        {
            var (vm, timeline) = Build(initial: 3);
            var (window, lines) = Show(vm);

            Assert.All(Enumerable.Range(0, 3), i => Assert.True(IsAtRest(Line(lines, i)), $"line {i} is an initial line"));

            timeline.Apply(Message(4, "arrived"));
            vm.Refresh();
            window.UpdateLayout();

            Assert.Equal(4, lines.ItemCount);
            Assert.False(IsAtRest(Line(lines, 3)), "the arrival plays its droplet");
            Assert.True(IsAtRest(Line(lines, 0)), "lines already shown stay still");
        });
    }

    [AvaloniaFact]
    public void A_refresh_with_no_new_message_animates_nothing_and_keeps_the_containers()
    {
        WithLiquid(() =>
        {
            var (vm, _) = Build(initial: 3);
            var (window, lines) = Show(vm);
            var before = lines.ContainerFromIndex(1);

            vm.Refresh();
            window.UpdateLayout();

            Assert.Same(before, lines.ContainerFromIndex(1));
            Assert.All(Enumerable.Range(0, 3), i => Assert.True(IsAtRest(Line(lines, i))));
        });
    }

    [AvaloniaFact]
    public void An_edited_line_is_replaced_in_place_without_an_arrival()
    {
        WithLiquid(() =>
        {
            var (vm, timeline) = Build(initial: 2);
            var (window, lines) = Show(vm);

            timeline.Apply(Message(2, "line 2 edited") with { Ev = 2, Edited = true });
            vm.Refresh();
            window.UpdateLayout();

            Assert.Equal("line 2 edited", vm.Lines[1].Text);
            Assert.Equal(2, lines.ItemCount);
            Assert.True(IsAtRest(Line(lines, 1)));
        });
    }

    [AvaloniaFact]
    public void The_view_model_appends_new_lines_instead_of_rebuilding_the_collection()
    {
        var (vm, timeline) = Build(initial: 2);
        var first = vm.Lines[0];

        timeline.Apply(Message(3, "third"));
        vm.Refresh();

        Assert.Equal(3, vm.Lines.Count);
        Assert.Same(first, vm.Lines[0]);
    }

    [AvaloniaFact]
    public void A_message_that_lands_before_shown_lines_rebuilds_the_collection_in_order()
    {
        var (vm, timeline) = Build(initial: 0);
        timeline.Apply(Message(5, "five"));
        vm.Refresh();

        timeline.Apply(Message(2, "two"));
        vm.Refresh();

        Assert.Equal([2L, 5L], vm.Lines.Select(l => l.N));
    }

    [AvaloniaFact]
    public void The_send_button_keeps_its_label_and_drops_the_theme_transitions()
    {
        var (vm, _) = Build(initial: 0);
        var (window, _) = Show(vm);
        var button = window.GetVisualDescendants().OfType<Button>().Single(b => b.Name == "SendButton");

        Assert.Contains(button.GetVisualDescendants().OfType<TextBlock>(), t => t.Text == "Send");
        Assert.Null(button.Transitions);
    }

    [AvaloniaFact]
    public void Message_motion_is_released_when_the_view_leaves_the_window()
    {
        WithLiquid(() =>
        {
            var (vm, timeline) = Build(initial: 1);
            var (window, _) = Show(vm);
            window.Content = null;
            Dispatcher.UIThread.RunJobs();

            timeline.Apply(Message(2, "later"));
            vm.Refresh();

            Assert.Equal(2, vm.Lines.Count);
        });
    }
}
