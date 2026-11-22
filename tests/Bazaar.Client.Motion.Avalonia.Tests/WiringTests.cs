using System.Collections.ObjectModel;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;

namespace Bazaar.Client.Motion.Avalonia.Tests;

public class WiringTests
{
    private sealed record Msg(string Text, bool Own = false);

    private static MotionSettings Liquid() => new(MotionPlatform.Phone) { Setting = MotionLevel.Liquid };

    private static (ListBox List, Window Window) ShowList(ObservableCollection<Msg> items)
    {
        var list = new ListBox
        {
            ItemsSource = items,
            ItemTemplate = new global::Avalonia.Controls.Templates.FuncDataTemplate<Msg>((m, _) => new TextBlock { Text = m?.Text }),
        };
        var window = new Window { Width = 300, Height = 300, Content = list };
        window.Show();
        return (list, window);
    }

    private static ListBoxItem Container(ListBox list, int index) => (ListBoxItem)list.ContainerFromIndex(index)!;

    private static bool IsAtRest(Control c) => (c.RenderTransform is null || c.RenderTransform.Value.IsIdentity) && c.Opacity == 1;

    [AvaloniaFact]
    public void Initial_load_does_not_animate_but_a_new_message_does()
    {
        var items = new ObservableCollection<Msg> { new("old 1"), new("old 2") };
        var (list, window) = ShowList(items);
        var motion = MessageListMotion.Attach(list, settings: Liquid(), driverFactory: _ => new ManualFrameDriver());
        Dispatcher.UIThread.RunJobs();

        // Attached after the first load: nothing in view animates.
        Assert.All(Enumerable.Range(0, 2), i => Assert.True(IsAtRest(Container(list, i))));

        items.Add(new Msg("new"));
        window.UpdateLayout();

        var fresh = Container(list, 2);
        Assert.False(IsAtRest(fresh), "the new message starts its droplet");
        Assert.True(IsAtRest(Container(list, 0)));
        motion.Dispose();
    }

    [AvaloniaFact]
    public void A_new_source_is_a_new_baseline_and_a_reset_animates_nothing()
    {
        var items = new ObservableCollection<Msg> { new("a") };
        var (list, window) = ShowList(items);
        var motion = MessageListMotion.Attach(list, settings: Liquid(), driverFactory: _ => new ManualFrameDriver());

        var other = new ObservableCollection<Msg> { new("b"), new("c") };
        list.ItemsSource = other;
        window.UpdateLayout();
        Assert.All(Enumerable.Range(0, 2), i => Assert.True(IsAtRest(Container(list, i))));

        other.Clear();
        other.Add(new Msg("d"));
        // A clear and a single add is still an arrival; the clear itself is not.
        window.UpdateLayout();
        Assert.False(IsAtRest(Container(list, 0)));
        motion.Dispose();
    }

    [AvaloniaFact]
    public void A_burst_of_new_items_is_a_history_load_and_does_not_animate()
    {
        var items = new ObservableCollection<Msg>();
        var (list, window) = ShowList(items);
        var motion = MessageListMotion.Attach(list, settings: Liquid(), driverFactory: _ => new ManualFrameDriver());

        for (var i = 0; i <= MotionHost.MaxAnimatedBurst; i++)
        {
            items.Add(new Msg($"history {i}"));
        }

        window.UpdateLayout();
        Assert.All(Enumerable.Range(0, items.Count), i => Assert.True(IsAtRest(Container(list, i))));
        motion.Dispose();
    }

    [AvaloniaFact]
    public void Scrolling_back_to_an_item_that_finished_animating_does_not_replay_it()
    {
        var items = new ObservableCollection<Msg> { new("a") };
        var (list, window) = ShowList(items);
        var driver = new ManualFrameDriver();
        var motion = MessageListMotion.Attach(list, settings: Liquid(), driverFactory: _ => driver);

        items.Add(new Msg("b"));
        window.UpdateLayout();
        for (var i = 0; i < 200 && driver.HasPending; i++)
        {
            driver.Step(TimeSpan.FromMilliseconds(16));
        }

        Assert.True(IsAtRest(Container(list, 1)));

        // Items that never got a container within the layout pass are dropped from the pending set by the flush.
        Dispatcher.UIThread.RunJobs();
        list.ItemsSource = null;
        list.ItemsSource = items;
        window.UpdateLayout();
        Assert.All(Enumerable.Range(0, 2), i => Assert.True(IsAtRest(Container(list, i))));
        motion.Dispose();
    }

    [AvaloniaFact]
    public void Own_messages_use_the_own_kind_and_start_below()
    {
        var items = new ObservableCollection<Msg>();
        var (list, window) = ShowList(items);
        var motion = MessageListMotion.Attach(list, m => ((Msg)m).Own ? Bazaar.Client.Motion.DropletKind.OwnSent : Bazaar.Client.Motion.DropletKind.Arrival,
            Liquid(), _ => new ManualFrameDriver());

        items.Add(new Msg("mine", Own: true));
        window.UpdateLayout();

        var frame = ((global::Avalonia.Media.MatrixTransform)Container(list, 0).RenderTransform!).Matrix;
        Assert.True(frame.M32 > 0, "an own message starts below its place");
        motion.Dispose();
    }

    [AvaloniaFact]
    public void Reduced_motion_still_marks_the_arrival_with_a_fade_only()
    {
        var settings = new MotionSettings(MotionPlatform.Desktop) { OsReducedMotion = true };
        var items = new ObservableCollection<Msg>();
        var (list, window) = ShowList(items);
        var motion = MessageListMotion.Attach(list, settings: settings, driverFactory: _ => new ManualFrameDriver());

        items.Add(new Msg("x"));
        window.UpdateLayout();

        var container = Container(list, 0);
        Assert.Null(container.RenderTransform);
        Assert.True(container.Opacity < 1);
        motion.Dispose();
    }

    [AvaloniaFact]
    public void Press_squashes_the_button_and_release_springs_it_back()
    {
        var driver = new ManualFrameDriver();
        var button = new Button { Content = "Go" };
        var window = new Window { Content = button };
        window.Show();
        var motion = ButtonMotion.Attach(button, Liquid(), _ => driver);

        motion.Press.Press();
        driver.Step(TimeSpan.FromMilliseconds(40));
        driver.Step(TimeSpan.FromMilliseconds(40));
        Assert.NotNull(button.RenderTransform);
        motion.Press.Release();
        for (var i = 0; i < 200 && driver.HasPending; i++)
        {
            driver.Step(TimeSpan.FromMilliseconds(16));
        }

        Assert.True(IsAtRest(button), "the button is back at rest");
        Assert.False(driver.HasPending, "nothing runs at rest");
        motion.Dispose();
    }

    [AvaloniaFact]
    public void The_original_content_is_kept_inside_the_wrapped_button()
    {
        var button = new Button { Content = "Go" };
        var window = new Window { Content = button };
        window.Show();
        var motion = ButtonMotion.Attach(button, Liquid(), _ => new ManualFrameDriver());

        Assert.Contains(button.GetVisualDescendants().OfType<TextBlock>(), t => t.Text == "Go");
        motion.Dispose();
    }

    [AvaloniaFact]
    public void Send_button_arms_with_text_launches_on_send_and_empties_after()
    {
        var driver = new ManualFrameDriver();
        var button = new Button { Content = "Send" };
        var draft = new TextBox();
        var window = new Window { Content = new StackPanel { Children = { draft, button } } };
        window.Show();
        var motion = ButtonMotion.AttachSend(button, draft, Liquid(), _ => driver);
        void Drain()
        {
            for (var i = 0; i < 400 && driver.HasPending; i++)
            {
                driver.Step(TimeSpan.FromMilliseconds(16));
            }
        }

        Drain();
        Assert.Equal(0, motion.Send!.Fill, 3);

        draft.Text = "hello";
        Drain();
        Assert.Equal(1, motion.Send.Fill, 3);

        motion.LaunchIfArmed();
        Assert.True(motion.Send.IsRunning, "the launch plays");
        draft.Text = string.Empty; // the send clears the draft mid-launch
        Assert.True(motion.Send.IsRunning, "clearing the draft does not cut the launch short");
        Drain();
        Assert.False(driver.HasPending, $"requests={driver.Requests} running={motion.Send.IsRunning} fill={motion.Send.Fill}");
        Assert.Equal(0, motion.Send.Fill, 3);
        Assert.True(IsAtRest(button));
        motion.Dispose();
    }

    [AvaloniaFact]
    public void Launch_does_nothing_when_the_draft_is_empty()
    {
        var button = new Button { Content = "Send" };
        var draft = new TextBox();
        var window = new Window { Content = new StackPanel { Children = { draft, button } } };
        window.Show();
        var driver = new ManualFrameDriver();
        var motion = ButtonMotion.AttachSend(button, draft, Liquid(), _ => driver);
        for (var i = 0; i < 400 && driver.HasPending; i++)
        {
            driver.Step(TimeSpan.FromMilliseconds(16));
        }

        motion.LaunchIfArmed();
        Assert.False(motion.Send!.IsRunning);
        motion.Dispose();
    }

    [AvaloniaFact]
    public void Reaction_chips_added_later_pop_and_the_first_ones_do_not()
    {
        var panel = new StackPanel();
        var first = new Border();
        panel.Children.Add(first);
        var motion = ReactionChipsMotion.Attach(panel, Liquid(), _ => new ManualFrameDriver());

        var added = new Border();
        panel.Children.Add(added);

        Assert.True(IsAtRest(first));
        Assert.False(IsAtRest(added));
        motion.Dispose();
    }

    [AvaloniaFact]
    public void Badge_pops_in_from_none_bumps_on_change_and_ignores_the_baseline()
    {
        var label = new TextBlock { Text = string.Empty };
        var badge = new Border { Child = label };
        var driver = new ManualFrameDriver();
        var motion = BadgeMotion.Attach(badge, label, Liquid(), _ => driver);
        Assert.False(driver.HasPending);

        label.Text = "1";
        Assert.True(driver.HasPending, "appear");
        for (var i = 0; i < 200 && driver.HasPending; i++)
        {
            driver.Step(TimeSpan.FromMilliseconds(16));
        }

        label.Text = "2";
        Assert.True(driver.HasPending, "bump");
        for (var i = 0; i < 200 && driver.HasPending; i++)
        {
            driver.Step(TimeSpan.FromMilliseconds(16));
        }

        label.Text = "0";
        Assert.False(driver.HasPending, "clearing the badge starts nothing");
        motion.Dispose();
    }

    [AvaloniaFact]
    public void Typing_dots_loop_only_while_visible_in_a_window()
    {
        var driver = new ManualFrameDriver();
        var dots = new TypingDots(Liquid(), _ => driver);
        Assert.False(dots.IsLooping);

        var window = new Window { Content = dots };
        window.Show();
        Assert.True(dots.IsLooping);
        Assert.True(driver.HasPending);

        dots.IsVisible = false;
        Assert.False(dots.IsLooping);
    }

    [AvaloniaFact]
    public void The_shared_settings_are_one_instance_and_can_be_replaced()
    {
        var before = MotionHost.Settings;
        Assert.Same(before, MotionHost.Settings);

        var own = Liquid();
        MotionHost.Use(own);
        try
        {
            Assert.Same(own, MotionHost.Settings);
        }
        finally
        {
            MotionHost.Use(before);
        }
    }

    [AvaloniaFact]
    public void Pressing_with_the_pointer_starts_press_and_ripple()
    {
        var driver = new ManualFrameDriver();
        var button = new Button { Content = "Go", HorizontalAlignment = global::Avalonia.Layout.HorizontalAlignment.Stretch, VerticalAlignment = global::Avalonia.Layout.VerticalAlignment.Stretch };
        var window = new Window { Content = button };
        window.Show();
        var motion = ButtonMotion.Attach(button, Liquid(), _ => driver);

        window.UpdateLayout();
        var inside = new global::Avalonia.Point(10, 10);
        window.MouseDown(inside, MouseButton.Left);
        Assert.True(motion.Press.IsRunning);
        Assert.True(motion.Ripple.IsRunning);
        window.MouseUp(inside, MouseButton.Left);
        motion.Dispose();
    }
}
