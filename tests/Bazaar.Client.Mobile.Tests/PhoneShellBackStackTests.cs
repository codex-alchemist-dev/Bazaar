using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Bazaar.Client.Abstractions.Panes;
using Bazaar.Client.BackStack;
using Bazaar.Client.BackStack.Abstractions;
using Bazaar.Client.Gestures;
using Bazaar.Client.Motion;
using Bazaar.Client.Motion.Avalonia;
using Bazaar.Client.Panes.Abstractions;

namespace Bazaar.Client.Mobile.Tests;

internal sealed class ManualFrames : IFrameDriver
{
    private Action<TimeSpan>? _pending;
    private TimeSpan _now;

    public bool HasPending => _pending is not null;

    public void RequestFrame(Action<TimeSpan> callback) => _pending = callback;

    public void RunToRest()
    {
        for (var i = 0; i < 300 && _pending is not null; i++)
        {
            _now += TimeSpan.FromMilliseconds(16);
            var callback = _pending;
            _pending = null;
            callback(_now);
        }
    }
}

public class PhoneShellBackStackTests
{
    private static readonly ScreenEntry _thread = new("thread", "t1");
    private static readonly ScreenEntry _profile = new("profile", "p1");

    private sealed class Rig
    {
        public required PhoneShell Shell { get; init; }

        public required ScreenHistory Stack { get; init; }

        public required ManualFrames Frames { get; init; }

        public required Window Window { get; init; }

        public required List<ScreenEntry> Built { get; init; }
    }

    private static Rig Build(IBackGestureSource? source, MotionSettings? motion = null)
    {
        var registry = new PaneRegistry(TestPaneViews.Instance);
        registry.Register("a", d => new GenericPaneViewModel(d));
        var stack = new ScreenHistory(new ScreenEntry("chat"));
        var frames = new ManualFrames();
        var built = new List<ScreenEntry>();
        var shell = new PhoneShell(
            registry,
            motion: motion,
            frames: frames,
            history: stack,
            screenView: e =>
            {
                built.Add(e);
                return new TextBlock { Text = e.Kind };
            },
            backSource: source);
        var window = new Window { Width = 400, Height = 800, Content = shell };
        window.Show();
        Dispatcher.UIThread.RunJobs();
        return new Rig { Shell = shell, Stack = stack, Frames = frames, Window = window, Built = built };
    }

    private static MotionSettings Reduced() => new(MotionPlatform.Phone) { Setting = MotionLevel.Reduced };

    [AvaloniaFact]
    public void At_the_root_there_is_no_back_host_and_back_is_not_handled()
    {
        var rig = Build(new BackGestureRelay());

        Assert.Null(rig.Shell.BackHost);
        Assert.False(rig.Shell.CanGoBack);
        Assert.False(rig.Shell.GoBack());
    }

    [AvaloniaFact]
    public void Pushing_a_screen_mounts_the_back_host_and_reports_can_go_back()
    {
        var rig = Build(new BackGestureRelay());
        var changes = new List<bool>();
        rig.Shell.CanGoBackChanged += (_, can) => changes.Add(can);

        rig.Stack.Push(_thread);

        Assert.NotNull(rig.Shell.BackHost);
        Assert.True(rig.Shell.CanGoBack);
        Assert.Equal([true], changes);

        rig.Stack.Pop();
        Assert.Null(rig.Shell.BackHost);
        Assert.Equal([true, false], changes);
    }

    [AvaloniaFact]
    public void A_back_swipe_scales_the_current_screen_and_the_one_beneath_then_a_commit_pops_the_stack()
    {
        var relay = new BackGestureRelay();
        var rig = Build(relay);
        rig.Stack.Push(_thread);
        var thread = rig.Shell.CurrentScreenView;

        relay.Start(0.1, BackGestureEdge.Left);
        relay.Progress(0.6, BackGestureEdge.Left);

        // The thread screen is scaled and shifted, and the chat pager beneath it is shown scaled up from its resting size.
        Assert.True(rig.Shell.BackHost!.Controller.IsActive);
        Assert.NotNull(thread.RenderTransform);
        Assert.NotNull(rig.Shell.Gestures.RenderTransform);
        Assert.True(rig.Shell.Gestures.IsVisible);
        Assert.Equal(_thread, rig.Stack.Current);

        relay.Commit(0.6, BackGestureEdge.Left);
        rig.Frames.RunToRest();

        Assert.Equal(1, rig.Stack.Depth);
        Assert.Null(rig.Shell.BackHost);
        Assert.Null(rig.Shell.Gestures.RenderTransform);
    }

    [AvaloniaFact]
    public void Committing_from_a_deeper_stack_reveals_the_previous_pushed_screen()
    {
        var relay = new BackGestureRelay();
        var rig = Build(relay);
        rig.Stack.Push(_thread);
        var thread = rig.Shell.CurrentScreenView;
        rig.Stack.Push(_profile);

        relay.Start(0.1, BackGestureEdge.Right);
        relay.Progress(0.5, BackGestureEdge.Right);
        Assert.NotNull(thread.RenderTransform);
        relay.Commit(0.5, BackGestureEdge.Right);
        rig.Frames.RunToRest();

        Assert.Equal(_thread, rig.Stack.Current);
        Assert.Same(thread, rig.Shell.CurrentScreenView);
        Assert.Null(thread.RenderTransform);
        Assert.NotNull(rig.Shell.BackHost);
        Assert.False(rig.Shell.BackHost.Controller.IsActive);
    }

    [AvaloniaFact]
    public void A_cancelled_back_swipe_keeps_the_stack_and_clears_the_transform()
    {
        var relay = new BackGestureRelay();
        var rig = Build(relay);
        rig.Stack.Push(_thread);

        relay.Start(0.1, BackGestureEdge.Left);
        relay.Progress(0.4, BackGestureEdge.Left);
        relay.Cancel(0.4, BackGestureEdge.Left);
        rig.Frames.RunToRest();

        Assert.Equal(2, rig.Stack.Depth);
        Assert.False(rig.Shell.BackHost!.Controller.IsActive);
        Assert.Equal(BackTransform.Identity, rig.Shell.BackHost.Controller.Transform);
    }

    [AvaloniaFact]
    public void Without_live_progress_back_is_an_instant_pop()
    {
        var fallback = new FallbackBackGestureSource();
        var rig = Build(fallback);
        rig.Stack.Push(_thread);

        fallback.Back();

        Assert.Equal(1, rig.Stack.Depth);
        Assert.False(rig.Frames.HasPending);
    }

    [AvaloniaFact]
    public void At_the_reduced_motion_level_a_commit_pops_at_once()
    {
        var relay = new BackGestureRelay();
        var rig = Build(relay, Reduced());
        rig.Stack.Push(_thread);

        relay.Start(0.1, BackGestureEdge.Left);
        relay.Commit(0.5, BackGestureEdge.Left);

        Assert.Equal(1, rig.Stack.Depth);
    }

    [AvaloniaFact]
    public void A_screen_is_built_once_and_kept_while_another_is_pushed_on_top()
    {
        var rig = Build(new BackGestureRelay());
        rig.Stack.Push(_thread);
        var threadView = rig.Shell.CurrentScreenView;
        rig.Stack.Push(_profile);
        rig.Stack.Pop();

        Assert.Same(threadView, rig.Shell.CurrentScreenView);
        Assert.Equal([_thread, _profile], rig.Built);
    }

    [AvaloniaFact]
    public void Go_back_pops_without_a_transition()
    {
        var rig = Build(null);
        rig.Stack.Push(_thread);

        Assert.Null(rig.Shell.BackHost);
        Assert.True(rig.Shell.GoBack());
        Assert.Equal(1, rig.Stack.Depth);
    }

    [AvaloniaFact]
    public void A_shell_without_a_stack_is_unchanged()
    {
        var registry = new PaneRegistry(TestPaneViews.Instance);
        registry.Register("a", d => new GenericPaneViewModel(d));
        var shell = new PhoneShell(registry);

        Assert.Null(shell.History);
        Assert.False(shell.CanGoBack);
        Assert.False(shell.GoBack());
    }
}
