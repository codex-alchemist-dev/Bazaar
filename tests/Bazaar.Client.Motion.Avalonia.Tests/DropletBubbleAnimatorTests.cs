using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Media;

namespace Bazaar.Client.Motion.Avalonia.Tests;

/// <summary>Collects frame requests so a test can run the animation by hand and see that nothing is requested at rest.</summary>
internal sealed class ManualFrameDriver : IFrameDriver
{
    private Action<TimeSpan>? _pending;
    private TimeSpan _now;

    public int Requests { get; private set; }

    public bool HasPending => _pending is not null;

    public void RequestFrame(Action<TimeSpan> callback)
    {
        Requests++;
        _pending = callback;
    }

    public void Step(TimeSpan delta)
    {
        _now += delta;
        var callback = _pending;
        _pending = null;
        callback?.Invoke(_now);
    }
}

public class DropletBubbleAnimatorTests
{
    private static readonly TimeSpan _frame = TimeSpan.FromMilliseconds(16);

    private static (Border Bubble, TextBlock Text, MotionSettings Settings, ManualFrameDriver Driver, DropletBubbleAnimator Animator) Create(MotionPlatform platform = MotionPlatform.Phone)
    {
        var text = new TextBlock { Text = "hello" };
        var bubble = new Border { Child = text, Width = 200, Height = 40 };
        var settings = new MotionSettings(platform);
        var driver = new ManualFrameDriver();
        return (bubble, text, settings, driver, new DropletBubbleAnimator(bubble, text, settings, driver));
    }

    private static List<MotionFrame> Run(ManualFrameDriver driver, DropletBubbleAnimator animator)
    {
        var frames = new List<MotionFrame> { animator.LastFrame };
        for (var i = 0; i < 200 && driver.HasPending; i++)
        {
            driver.Step(_frame);
            frames.Add(animator.LastFrame);
        }

        return frames;
    }

    [AvaloniaFact]
    public void The_phone_default_plays_the_liquid_droplet()
    {
        var (bubble, text, _, driver, animator) = Create(MotionPlatform.Phone);

        animator.Play(DropletKind.Arrival, 0, 240);

        Assert.Equal(MotionLevel.Liquid, animator.PlayedLevel);
        Assert.Equal(0, text.Opacity); // text hidden until the shape settles
        Assert.NotNull(bubble.RenderTransform);

        var frames = Run(driver, animator);
        Assert.Contains(frames, f => f.Ty > 1 && f.Scale < 0.5);
        Assert.Contains(frames, f => f.Scale > 1.0);
    }

    [AvaloniaFact]
    public void The_level_switch_changes_the_animation()
    {
        var (_, _, settings, driver, animator) = Create(MotionPlatform.Phone);

        animator.Play(DropletKind.Arrival, 0, 240);
        var liquid = Run(driver, animator);
        settings.Setting = MotionLevel.Subtle;
        animator.Play(DropletKind.Arrival, 0, 240);
        var subtle = Run(driver, animator);
        settings.Setting = MotionLevel.Reduced;
        animator.Play(DropletKind.Arrival, 0, 240);
        var reduced = Run(driver, animator);

        Assert.True(liquid.Count > subtle.Count);
        Assert.True(subtle.Count > reduced.Count);
        Assert.Equal(MotionLevel.Reduced, animator.PlayedLevel);
        Assert.Contains(subtle, f => f.Scale is > 0.95 and < 1.0);
        Assert.DoesNotContain(subtle, f => f.Ty != 0);
    }

    [AvaloniaFact]
    public void Reduced_has_no_travel_no_scale_and_a_short_fade()
    {
        var (bubble, _, settings, driver, animator) = Create(MotionPlatform.Phone);
        settings.Setting = MotionLevel.Reduced;

        animator.Play(DropletKind.Arrival, 120, 300);
        var frames = new List<MotionFrame> { animator.LastFrame };
        var transformSeen = bubble.RenderTransform is not null;
        while (driver.HasPending)
        {
            driver.Step(_frame);
            frames.Add(animator.LastFrame);
            transformSeen |= bubble.RenderTransform is not null;
        }

        Assert.False(transformSeen, "a view that only fades carries no transform");
        Assert.All(frames, f =>
        {
            Assert.Equal(0, f.Tx);
            Assert.Equal(0, f.Ty);
            Assert.Equal(1, f.Scale);
        });
        Assert.True(_frame * (frames.Count - 1) <= TimeSpan.FromMilliseconds(90) + _frame * 2);
    }

    [AvaloniaFact]
    public void The_os_reduced_motion_setting_forces_reduced_even_when_liquid_is_chosen()
    {
        var (_, _, settings, driver, animator) = Create(MotionPlatform.Phone);
        settings.Setting = MotionLevel.Liquid;
        settings.OsReducedMotion = true;

        animator.Play(DropletKind.OwnSent, 0, 240);
        var frames = Run(driver, animator);

        Assert.Equal(MotionLevel.Reduced, settings.Effective);
        Assert.Equal(MotionLevel.Reduced, animator.PlayedLevel);
        Assert.All(frames, f => Assert.Equal(0, f.Ty));
    }

    [AvaloniaFact]
    public void Desktop_defaults_to_subtle()
    {
        var (_, _, _, driver, animator) = Create(MotionPlatform.Desktop);

        animator.Play(DropletKind.Arrival, 0, 240);

        Assert.Equal(MotionLevel.Subtle, animator.PlayedLevel);
        Assert.NotNull(driver);
    }

    [AvaloniaFact]
    public void Ends_at_rest_with_no_transform_and_requests_no_more_frames()
    {
        var (bubble, text, _, driver, animator) = Create();
        var completed = 0;
        animator.Completed += (_, _) => completed++;

        animator.Play(DropletKind.Arrival, 0, 240);
        Run(driver, animator);
        var requests = driver.Requests;

        Assert.False(animator.IsRunning);
        Assert.False(driver.HasPending, "no work at rest");
        Assert.Null(bubble.RenderTransform);
        Assert.Equal(1, bubble.Opacity);
        Assert.Equal(1, text.Opacity);
        Assert.Equal(1, completed);

        driver.Step(_frame);
        Assert.Equal(requests, driver.Requests);
    }

    [AvaloniaFact]
    public void An_interrupt_eases_to_rest_instead_of_jumping()
    {
        var (bubble, _, _, driver, animator) = Create();
        animator.Play(DropletKind.Arrival, 0, 240);
        driver.Step(_frame);
        driver.Step(_frame);
        driver.Step(_frame);
        var inFlight = animator.LastFrame;
        Assert.True(inFlight.Ty > 10);

        animator.Interrupt();

        Assert.True(animator.IsRunning, "still easing");
        Assert.Equal(inFlight.Ty, animator.LastFrame.Ty);
        var frames = Run(driver, animator);
        Assert.True(frames.Count > 2);
        Assert.False(animator.IsRunning);
        Assert.Null(bubble.RenderTransform);
    }

    [AvaloniaFact]
    public void A_settings_change_mid_flight_interrupts_the_animation()
    {
        var (_, _, settings, driver, animator) = Create();
        animator.Play(DropletKind.Arrival, 0, 240);
        driver.Step(_frame);
        driver.Step(_frame);

        settings.Setting = MotionLevel.Reduced;

        Assert.True(animator.IsRunning); // easing out from the liquid values
        Run(driver, animator);
        Assert.False(animator.IsRunning);
        Assert.Equal(MotionLevel.Liquid, animator.PlayedLevel); // the running one finished at its own level
    }

    [AvaloniaFact]
    public void Playing_again_while_running_restarts_cleanly()
    {
        var (bubble, _, _, driver, animator) = Create();
        animator.Play(DropletKind.Arrival, 0, 240);
        driver.Step(_frame);

        animator.Play(DropletKind.OwnSent, 0, 240);
        Run(driver, animator);

        Assert.False(animator.IsRunning);
        Assert.Null(bubble.RenderTransform);
        Assert.Equal(1, bubble.Opacity);
    }

    [AvaloniaFact]
    public void Only_transform_and_opacity_are_touched()
    {
        var (bubble, _, _, driver, animator) = Create();
        var width = bubble.Width;
        var margin = bubble.Margin;

        animator.Play(DropletKind.Arrival, 30, 240);
        driver.Step(_frame);
        driver.Step(_frame);

        Assert.IsType<MatrixTransform>(bubble.RenderTransform);
        Assert.Equal(width, bubble.Width);
        Assert.Equal(margin, bubble.Margin);
    }

    private sealed class FakeGoo : IGooMergeEffect
    {
        public int Calls { get; private set; }

        public bool IsAvailable => true;

        public bool TryApply(in GooMergeRequest request)
        {
            Calls++;
            return true;
        }
    }

    [AvaloniaFact]
    public void The_goo_effect_runs_only_at_the_liquid_level()
    {
        var text = new TextBlock();
        var bubble = new Border { Child = text };
        var settings = new MotionSettings(MotionPlatform.Phone);
        var driver = new ManualFrameDriver();
        var goo = new FakeGoo();
        var animator = new DropletBubbleAnimator(bubble, text, settings, driver, goo);

        animator.Play(DropletKind.Arrival, 0, 240);
        Run(driver, animator);
        var liquidCalls = goo.Calls;

        settings.Setting = MotionLevel.Subtle;
        animator.Play(DropletKind.Arrival, 0, 240);
        Run(driver, animator);

        Assert.True(liquidCalls > 0);
        Assert.Equal(liquidCalls, goo.Calls);
    }
}

public class TypingIndicatorAnimatorTests
{
    private static (Border[] Dots, MotionSettings Settings, ManualFrameDriver Driver, TypingIndicatorAnimator Animator) Create()
    {
        var dots = new[] { new Border(), new Border(), new Border() };
        var settings = new MotionSettings(MotionPlatform.Phone);
        var driver = new ManualFrameDriver();
        return (dots, settings, driver, new TypingIndicatorAnimator(dots, settings, driver, 12));
    }

    [AvaloniaFact]
    public void Runs_only_between_start_and_stop_and_leaves_no_transform_at_rest()
    {
        var (dots, _, driver, animator) = Create();

        animator.Start();
        driver.Step(TimeSpan.FromMilliseconds(16));
        driver.Step(TimeSpan.FromMilliseconds(300));
        Assert.True(driver.HasPending);

        animator.Stop();
        Assert.False(animator.IsRunning);
        Assert.All(dots, d => Assert.Null(d.RenderTransform));
        driver.Step(TimeSpan.FromMilliseconds(16));
        Assert.False(driver.HasPending);
    }

    [AvaloniaFact]
    public void The_level_switch_changes_how_the_dots_move()
    {
        var (dots, settings, driver, animator) = Create();
        animator.Start();

        driver.Step(TimeSpan.FromMilliseconds(16));
        driver.Step(TimeSpan.FromMilliseconds(584)); // mid-cycle
        var liquidOuter = ((MatrixTransform)dots[0].RenderTransform!).Matrix.M31;
        Assert.True(Math.Abs(liquidOuter) < 11, "liquid: the outer drop has moved toward the middle");

        settings.Setting = MotionLevel.Reduced;
        driver.Step(TimeSpan.FromMilliseconds(16));
        var reduced = (MatrixTransform)dots[0].RenderTransform!;
        Assert.Equal(0, reduced.Matrix.M31);
        Assert.Equal(0, reduced.Matrix.M32);
        animator.Stop();
    }

    [AvaloniaFact]
    public void Needs_exactly_three_dots()
    {
        var settings = new MotionSettings(MotionPlatform.Phone);

        Assert.Throws<ArgumentOutOfRangeException>(() => new TypingIndicatorAnimator([new Border()], settings, new ManualFrameDriver(), 12));
    }
}

public class MotionSettingsTests
{
    [AvaloniaFact]
    public void Changes_raise_the_event_and_tokens_keep_the_floor()
    {
        var settings = new MotionSettings(MotionPlatform.Phone);
        var raised = 0;
        settings.Changed += (_, _) => raised++;

        settings.Setting = MotionLevel.Subtle;
        settings.Setting = MotionLevel.Subtle;
        settings.OsReducedMotion = true;
        settings.Tokens = new MotionTokens { ReducedFade = TimeSpan.FromSeconds(1), MaxStretch = 1.2 };

        Assert.Equal(3, raised);
        Assert.Equal(TimeSpan.FromMilliseconds(90), settings.Tokens.ReducedFade);
        Assert.Equal(MotionLevel.Reduced, settings.Effective);
    }
}
