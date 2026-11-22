using Avalonia.Controls;
using Avalonia.Headless;
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

/// <summary>Reaction chips and the typing row: view model state, and the animators the view attaches to them.</summary>
public class ChatStatesTests
{
    private static readonly Guid _gc = Guid.Parse("0196a6f0-0000-7000-8000-000000000001");

    private static WireGcMessage Message(long n, string text) =>
        new(_gc, n, 1, Guid.NewGuid(), Guid.Empty, text, DateTimeOffset.UnixEpoch, false, [], null);

    private static (ChatViewModel Vm, InMemoryChatReactions Reactions, ManualTypingSource Typing) Build(int initial)
    {
        var transport = new FakeWireTransport();
        var timeline = new ChatTimeline(new Dictionary<Guid, ChatMember> { [Guid.Empty] = new("Charles", IsAgent: true) }, "me", TimeZoneInfo.Utc);
        for (var i = 1; i <= initial; i++) timeline.Apply(Message(i, $"line {i}"));

        var client = new WireClient(transport, transport.Codec, new InMemoryCursorStore(),
            new WireClientOptions { DeviceId = "linux-test", DeviceClass = "linux", ClientBuild = "test" }, new FakeClock());
        var session = new GcChatSession(client, new ChatConnectionSettings(new Uri("ws://h/ws"), "t", _gc, "me"), timeline);
        var reactions = new InMemoryChatReactions();
        var typing = new ManualTypingSource();
        return (new ChatViewModel(session, reactions: reactions, typing: typing), reactions, typing);
    }

    private static (Window Window, ListBox Lines) Show(ChatViewModel vm)
    {
        var window = new Window { Width = 400, Height = 400, Content = ChatViewFactory.Instance.Create(vm) };
        window.Show();
        Dispatcher.UIThread.RunJobs();
        return (window, window.GetVisualDescendants().OfType<ListBox>().Single());
    }

    private static ReactionStrip Strip(ListBox lines, int index) =>
        ((Control)lines.ContainerFromIndex(index)!).GetVisualDescendants().OfType<ReactionStrip>().Single();

    private static IEnumerable<Border> Chips(ReactionStrip strip) => strip.Children.OfType<Border>();

    private static string CountText(Border chip) => chip.GetVisualDescendants().OfType<TextBlock>().Last().Text!;

    private static bool IsAtRest(Control c) => (c.RenderTransform is null || c.RenderTransform.Value.IsIdentity) && c.Opacity == 1;

    // A bump starts at rest with velocity, so it shows only once a frame has run: tick the render timer until the control moves.
    private static bool PlaysWithin(Func<bool> moved)
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
    public void Reactions_reach_the_lines_as_a_stable_list_that_follows_the_source()
    {
        var (vm, reactions, _) = Build(initial: 2);
        var list = vm.Lines[0].Reactions;
        Assert.Empty(list);

        reactions.Set(1, "+1", 2, mine: true);
        reactions.Set(1, "ok", 1);
        Assert.Same(list, vm.Lines[0].Reactions);
        Assert.Equal([new ChatReaction("+1", 2, true), new ChatReaction("ok", 1, false)], list);

        reactions.Set(1, "+1", 3, mine: true);
        reactions.Set(1, "ok", 0);
        Assert.Equal([new ChatReaction("+1", 3, true)], list);
        Assert.Empty(vm.Lines[1].Reactions);
    }

    [Fact]
    public void A_refresh_keeps_the_same_reaction_list_and_does_not_replace_the_line()
    {
        var (vm, reactions, _) = Build(initial: 2);
        reactions.Set(2, "+1", 1);
        var line = vm.Lines[1];

        vm.Refresh();

        Assert.Same(line, vm.Lines[1]);
    }

    [Fact]
    public void Typing_state_follows_the_source_and_names_who_types()
    {
        var (vm, _, typing) = Build(initial: 0);
        Assert.False(vm.IsTyping);
        Assert.Equal(string.Empty, vm.TypingLabel);

        typing.Set("Charles");
        Assert.True(vm.IsTyping);
        Assert.Equal("Charles is typing", vm.TypingLabel);

        typing.Set("Charles", "Dom-tron");
        Assert.Equal("Charles and Dom-tron are typing", vm.TypingLabel);

        typing.Set("a", "b", "c");
        Assert.Equal("Several people are typing", vm.TypingLabel);

        typing.Set();
        Assert.False(vm.IsTyping);
    }

    [AvaloniaFact]
    public void Chips_present_at_load_stay_still_and_a_new_chip_pops_in()
    {
        WithLiquid(() =>
        {
            var (vm, reactions, _) = Build(initial: 1);
            reactions.Set(1, "+1", 2);
            vm.Refresh();
            var (_, lines) = Show(vm);
            var strip = Strip(lines, 0);

            Assert.Equal(["+1"], Chips(strip).Select(c => (string)c.Tag!));
            Assert.All(Chips(strip), c => Assert.True(IsAtRest(c), "the loaded chip does not animate"));

            reactions.Set(1, "ok", 1);

            var added = Chips(strip).Single(c => (string)c.Tag! == "ok");
            Assert.False(IsAtRest(added), "the added chip plays its pop");
            Assert.True(IsAtRest(Chips(strip).Single(c => (string)c.Tag! == "+1")));
        });
    }

    [AvaloniaFact]
    public void A_count_change_bumps_the_chip_and_a_removed_chip_leaves()
    {
        WithLiquid(() =>
        {
            var (vm, reactions, _) = Build(initial: 1);
            reactions.Set(1, "+1", 2);
            reactions.Set(1, "ok", 1);
            vm.Refresh();
            var (_, lines) = Show(vm);
            var strip = Strip(lines, 0);
            var plusOne = Chips(strip).Single(c => (string)c.Tag! == "+1");

            reactions.Set(1, "+1", 3);

            Assert.Equal("3", CountText(plusOne));
            Assert.True(PlaysWithin(() => !IsAtRest(plusOne)), "the count bump plays");

            reactions.Set(1, "ok", 0);

            Assert.Equal(["+1"], Chips(strip).Select(c => (string)c.Tag!));
        });
    }

    [AvaloniaFact]
    public void The_typing_row_is_hidden_until_someone_types_and_the_dots_loop_only_while_shown()
    {
        var (vm, _, typing) = Build(initial: 0);
        var (window, _) = Show(vm);
        var row = window.GetVisualDescendants().OfType<StackPanel>().Single(p => p.Name == "TypingRow");
        var dots = row.GetVisualDescendants().OfType<TypingDots>().Single();

        Assert.False(row.IsVisible);
        Assert.False(dots.IsLooping);

        typing.Set("Charles");
        Dispatcher.UIThread.RunJobs();

        Assert.True(row.IsVisible);
        Assert.True(dots.IsLooping);
        Assert.Contains(row.GetVisualDescendants().OfType<TextBlock>(), t => t.Text == "Charles is typing");

        typing.Set();
        Dispatcher.UIThread.RunJobs();

        Assert.False(row.IsVisible);
        Assert.False(dots.IsLooping);
    }
}
