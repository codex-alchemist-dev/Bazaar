using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using Bazaar.Client.Motion;
using Bazaar.Client.Motion.Avalonia;

namespace Bazaar.Client.Gestures.Avalonia.Tests;

internal sealed class ManualFrameDriver : IFrameDriver
{
    private Action<TimeSpan>? _pending;
    private TimeSpan _now;

    public bool HasPending => _pending is not null;

    public void RequestFrame(Action<TimeSpan> callback) => _pending = callback;

    public void Step(TimeSpan delta)
    {
        _now += delta;
        var callback = _pending;
        _pending = null;
        callback?.Invoke(_now);
    }

    public void RunToRest()
    {
        for (var i = 0; i < 300 && HasPending; i++)
        {
            Step(TimeSpan.FromMilliseconds(16));
        }
    }
}

public class SpringAnimationTests
{
    private sealed class Clock
    {
        public TimeSpan Now { get; set; }
    }

    private static double X(Control c) => c.RenderTransform is TranslateTransform t ? t.X : 0;

    [AvaloniaFact]
    public void Follow_moves_the_control_and_cancel_springs_back_then_rests()
    {
        var box = new Border();
        var driver = new ManualFrameDriver();
        var animator = new SpringTranslateAnimator(box, driver, new MotionSettings(MotionPlatform.Phone));

        animator.Follow(-60);
        Assert.Equal(-60, X(box));
        Assert.False(driver.HasPending);

        animator.Cancel();
        Assert.True(driver.HasPending);
        driver.Step(TimeSpan.FromMilliseconds(16));
        driver.Step(TimeSpan.FromMilliseconds(16));
        Assert.True(X(box) > -60);
        driver.RunToRest();
        Assert.Null(box.RenderTransform);
        Assert.False(animator.IsAnimating);
        Assert.False(driver.HasPending);
    }

    [AvaloniaFact]
    public void Release_velocity_carries_into_the_settle()
    {
        var box = new Border();
        var driver = new ManualFrameDriver();
        var clock = new Clock();
        var animator = new SpringTranslateAnimator(box, driver, new MotionSettings(MotionPlatform.Phone), clock: () => clock.Now);

        // Moving right at 2000 dp/s when released: the first frames carry on to the right before turning back.
        animator.Follow(0);
        clock.Now = TimeSpan.FromMilliseconds(16);
        animator.Follow(32);
        clock.Now = TimeSpan.FromMilliseconds(32);
        animator.Follow(64);
        animator.Cancel();
        driver.Step(TimeSpan.FromMilliseconds(16));
        driver.Step(TimeSpan.FromMilliseconds(16));
        Assert.True(X(box) > 64);
    }

    [AvaloniaFact]
    public void Commit_springs_the_new_page_in_from_the_side_it_was_swiped_to()
    {
        var box = new Border();
        var driver = new ManualFrameDriver();
        var animator = new SpringTranslateAnimator(box, driver, new MotionSettings(MotionPlatform.Phone));

        animator.Follow(-120);
        animator.Commit(400, 1);
        Assert.Equal(280, X(box));
        driver.RunToRest();
        Assert.Null(box.RenderTransform);
    }

    [AvaloniaFact]
    public void Reduced_motion_clears_the_transform_at_once()
    {
        var box = new Border();
        var driver = new ManualFrameDriver();
        var motion = new MotionSettings(MotionPlatform.Phone) { OsReducedMotion = true };
        var animator = new SpringTranslateAnimator(box, driver, motion);

        animator.Follow(-120);
        animator.Commit(400, 1);
        Assert.Null(box.RenderTransform);
        Assert.False(driver.HasPending);
    }

    [AvaloniaFact]
    public void Back_host_applies_the_transform_and_pops_after_the_commit_spring()
    {
        var current = new Border();
        var behind = new Border();
        var driver = new ManualFrameDriver();
        var relay = new BackGestureRelay();
        using var controller = new PredictiveBackController(relay, 400);
        using var host = new PredictiveBackHost(current, behind, controller, driver);
        var pops = 0;
        host.PopRequested += (_, _) => pops++;

        relay.Start(0, BackGestureEdge.Left);
        relay.Progress(0.5, BackGestureEdge.Left);
        Assert.IsType<TransformGroup>(current.RenderTransform);
        var expected = PredictiveBackMapper.Map(0.5, BackGestureEdge.Left, 400, GestureSettings.Default);
        Assert.Equal(expected.BehindScale, ((ScaleTransform)behind.RenderTransform!).ScaleX, 9);

        relay.Commit(0.5, BackGestureEdge.Left);
        driver.RunToRest();
        Assert.Equal(1, pops);
        Assert.Null(current.RenderTransform);
        Assert.Null(behind.RenderTransform);
    }

    [AvaloniaFact]
    public void Back_host_cancel_returns_to_rest_without_pop()
    {
        var current = new Border();
        var driver = new ManualFrameDriver();
        var relay = new BackGestureRelay();
        using var controller = new PredictiveBackController(relay, 400);
        using var host = new PredictiveBackHost(current, null, controller, driver);
        var pops = 0;
        host.PopRequested += (_, _) => pops++;

        relay.Start(0, BackGestureEdge.Right);
        relay.Progress(0.8, BackGestureEdge.Right);
        relay.Cancel(0.8, BackGestureEdge.Right);
        driver.RunToRest();
        Assert.Equal(0, pops);
        Assert.Null(current.RenderTransform);
    }

    [AvaloniaFact]
    public void Fallback_source_pops_without_any_frames()
    {
        var current = new Border();
        var driver = new ManualFrameDriver();
        var source = new FallbackBackGestureSource();
        using var controller = new PredictiveBackController(source, 400);
        using var host = new PredictiveBackHost(current, null, controller, driver);
        var pops = 0;
        host.PopRequested += (_, _) => pops++;

        source.Back();
        Assert.Equal(1, pops);
        Assert.False(driver.HasPending);
    }
}
