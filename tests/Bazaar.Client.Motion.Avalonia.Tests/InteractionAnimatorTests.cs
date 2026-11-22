using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;

namespace Bazaar.Client.Motion.Avalonia.Tests;

public class InteractionAnimatorTests
{
    private static readonly TimeSpan _frame = TimeSpan.FromMilliseconds(16);

    private static MotionSettings Settings(MotionLevel level)
    {
        var settings = new MotionSettings(MotionPlatform.Phone) { Setting = level };
        return settings;
    }

    private static List<InteractionFrame> Run(ManualFrameDriver driver, InteractionAnimator animator)
    {
        var frames = new List<InteractionFrame> { animator.LastFrame };
        for (var i = 0; i < 200 && driver.HasPending; i++)
        {
            driver.Step(_frame);
            frames.Add(animator.LastFrame);
        }

        return frames;
    }

    [AvaloniaFact]
    public void Ripple_grows_a_ring_from_the_press_point_and_hides_it_after()
    {
        var ring = new Border();
        var tint = new Border { Opacity = 0 };
        var driver = new ManualFrameDriver();
        var animator = new RippleAnimator(ring, tint, Settings(MotionLevel.Liquid), driver);

        animator.Play(new Point(30, 40), 100);
        Assert.Equal(200, ring.Width);
        Assert.NotNull(ring.RenderTransform);
        Assert.True(ring.Opacity > 0);

        var frames = Run(driver, animator);

        Assert.Contains(frames, f => f.Scale > 0.9);
        Assert.False(animator.IsRunning);
        Assert.False(driver.HasPending, "no work at rest");
        Assert.Equal(0, ring.Opacity);
        Assert.Equal(0, tint.Opacity);
    }

    [AvaloniaFact]
    public void Ripple_at_the_subtle_level_tints_and_never_shows_the_ring()
    {
        var ring = new Border();
        var tint = new Border { Opacity = 0 };
        var driver = new ManualFrameDriver();
        var animator = new RippleAnimator(ring, tint, Settings(MotionLevel.Subtle), driver);

        animator.Play(new Point(10, 10), 50);
        var tintSeen = 0.0;
        while (driver.HasPending)
        {
            driver.Step(_frame);
            tintSeen = Math.Max(tintSeen, tint.Opacity);
            Assert.Equal(0, ring.Opacity);
        }

        Assert.True(tintSeen > 0.05);
        Assert.Equal(0, tint.Opacity);
    }

    [AvaloniaFact]
    public void Toggle_moves_the_knob_stretches_it_and_ends_on_the_end_stop()
    {
        var knob = new Border { Width = 20, Height = 20 };
        var track = new Border { Opacity = 0 };
        var driver = new ManualFrameDriver();
        var animator = new ToggleAnimator(knob, track, 24, Settings(MotionLevel.Liquid), driver);
        animator.Set(false);

        animator.Toggle(true);
        var frames = Run(driver, animator);

        Assert.True(animator.IsChecked);
        Assert.Contains(frames, f => f.Along > 1.05);
        Assert.Contains(frames, f => f.Progress > 1.0);
        Assert.Equal(1, animator.Position);
        Assert.Equal(1, track.Opacity);
        var matrix = Assert.IsType<global::Avalonia.Media.MatrixTransform>(knob.RenderTransform).Matrix;
        Assert.Equal(24, matrix.M31, 6);
    }

    [AvaloniaFact]
    public void Toggle_flipped_mid_flight_continues_from_where_the_knob_is()
    {
        var knob = new Border { Width = 20, Height = 20 };
        var driver = new ManualFrameDriver();
        var animator = new ToggleAnimator(knob, null, 24, Settings(MotionLevel.Liquid), driver);
        animator.Set(false);

        animator.Toggle(true);
        driver.Step(_frame);
        driver.Step(_frame);
        driver.Step(_frame);
        var before = animator.Position;
        Assert.InRange(before, 0.01, 0.99);

        animator.Toggle(false);

        Assert.Equal(before, animator.Position, 9);
        Run(driver, animator);
        Assert.Equal(0, animator.Position);
        Assert.False(animator.IsChecked);
    }

    [AvaloniaFact]
    public void Toggle_at_the_reduced_level_is_instant_and_asks_for_no_frames()
    {
        var knob = new Border { Width = 20, Height = 20 };
        var driver = new ManualFrameDriver();
        var animator = new ToggleAnimator(knob, null, 24, Settings(MotionLevel.Reduced), driver);
        animator.Set(false);

        animator.Toggle(true);

        Assert.False(animator.IsRunning);
        Assert.Equal(0, driver.Requests);
        Assert.Equal(1, animator.Position);
    }

    [AvaloniaFact]
    public void Press_squashes_while_held_and_springs_back_to_no_transform()
    {
        var button = new Border { Width = 80, Height = 32 };
        var driver = new ManualFrameDriver();
        var animator = new PressAnimator(button, null, Settings(MotionLevel.Liquid), driver);

        animator.Press();
        var down = Run(driver, animator);
        Assert.Equal(0.96, down[^1].Across, 6);
        Assert.NotNull(button.RenderTransform);

        animator.Release();
        var up = Run(driver, animator);

        Assert.Contains(up, f => f.Across > 1.0);
        Assert.Null(button.RenderTransform);
        Assert.False(driver.HasPending);
    }

    [AvaloniaFact]
    public void Press_at_the_subtle_level_darkens_and_does_not_move()
    {
        var button = new Border { Width = 80, Height = 32 };
        var darken = new Border { Opacity = 0 };
        var driver = new ManualFrameDriver();
        var animator = new PressAnimator(button, darken, Settings(MotionLevel.Subtle), driver);

        animator.Press();
        Run(driver, animator);

        Assert.Equal(0.12, darken.Opacity, 6);
        Assert.Null(button.RenderTransform);

        animator.Release();
        Run(driver, animator);
        Assert.Equal(0, darken.Opacity);
    }

    [AvaloniaFact]
    public void Send_button_fills_when_armed_and_launches_then_rests()
    {
        var button = new Border { Width = 40, Height = 40 };
        var fill = new Border { Opacity = 0 };
        var driver = new ManualFrameDriver();
        var animator = new SendButtonAnimator(button, fill, Settings(MotionLevel.Liquid), driver);

        animator.Arm(true);
        Run(driver, animator);
        Assert.Equal(1, animator.Fill, 9);
        Assert.Equal(1, fill.Opacity);

        animator.Launch();
        var frames = Run(driver, animator);

        Assert.Contains(frames, f => f.Ty < -10);
        Assert.Null(button.RenderTransform);
        Assert.Equal(1, button.Opacity);
        Assert.Equal(1, fill.Opacity); // still armed until the composer clears
        Assert.False(driver.HasPending);

        animator.Arm(false);
        Run(driver, animator);
        Assert.Equal(0, fill.Opacity);
    }

    [AvaloniaFact]
    public void Arming_during_a_launch_puts_the_button_back()
    {
        var button = new Border { Width = 40, Height = 40 };
        var driver = new ManualFrameDriver();
        var animator = new SendButtonAnimator(button, new Border(), Settings(MotionLevel.Liquid), driver);

        animator.Launch();
        for (var i = 0; i < 12; i++)
        {
            driver.Step(_frame);
        }

        Assert.NotNull(button.RenderTransform);
        animator.Arm(false);

        Assert.Null(button.RenderTransform);
        Assert.Equal(1, button.Opacity);
    }

    [AvaloniaFact]
    public void Reaction_chip_pops_from_the_edge_and_ends_at_rest()
    {
        var chip = new Border { Width = 40, Height = 20 };
        var driver = new ManualFrameDriver();
        var animator = new ReactionPopAnimator(chip, Settings(MotionLevel.Liquid), driver);

        animator.Play(-30, 10);
        Assert.Equal(0, chip.Opacity);
        var frames = Run(driver, animator);

        Assert.Contains(frames, f => f.Scale > 1.0);
        Assert.Null(chip.RenderTransform);
        Assert.Equal(1, chip.Opacity);
    }

    [AvaloniaFact]
    public void Reaction_chip_at_the_reduced_level_only_fades()
    {
        var chip = new Border { Width = 40, Height = 20 };
        var driver = new ManualFrameDriver();
        var animator = new ReactionPopAnimator(chip, Settings(MotionLevel.Reduced), driver);

        animator.Play(-30, 10);
        var transformSeen = false;
        while (driver.HasPending)
        {
            driver.Step(_frame);
            transformSeen |= chip.RenderTransform is not null;
        }

        Assert.False(transformSeen);
        Assert.Equal(1, chip.Opacity);
    }

    [AvaloniaFact]
    public void Badge_pops_in_and_bumps_on_a_count_change()
    {
        var badge = new Border { Width = 18, Height = 18 };
        var driver = new ManualFrameDriver();
        var animator = new BadgeBumpAnimator(badge, Settings(MotionLevel.Liquid), driver);

        animator.Appear();
        var appear = Run(driver, animator);
        Assert.Equal(0, appear[0].Scale);
        Assert.Null(badge.RenderTransform);

        animator.Bump();
        var bump = Run(driver, animator);
        Assert.Contains(bump, f => f.Scale > 1.05);
        Assert.Null(badge.RenderTransform);
        Assert.Equal(1, badge.Opacity);
    }

    [AvaloniaFact]
    public void A_settings_change_mid_flight_ends_the_animation_at_rest()
    {
        var chip = new Border { Width = 40, Height = 20 };
        var settings = Settings(MotionLevel.Liquid);
        var driver = new ManualFrameDriver();
        var animator = new ReactionPopAnimator(chip, settings, driver);
        animator.Play(-30, 10);
        driver.Step(_frame);
        driver.Step(_frame);

        settings.Setting = MotionLevel.Reduced;

        Assert.False(animator.IsRunning);
        Assert.Null(chip.RenderTransform);
        Assert.Equal(1, chip.Opacity);
    }

    [AvaloniaFact]
    public void A_turned_off_preset_does_nothing_and_asks_for_no_frames()
    {
        var chip = new Border { Width = 40, Height = 20 };
        var settings = Settings(MotionLevel.Liquid);
        settings.Presets = settings.Presets.Without(InteractionNames.ReactionPop);
        var driver = new ManualFrameDriver();
        var animator = new ReactionPopAnimator(chip, settings, driver);

        animator.Play(-30, 10);

        Assert.False(animator.IsRunning);
        Assert.Equal(0, driver.Requests);
        Assert.Equal(1, chip.Opacity);
        Assert.Null(chip.RenderTransform);
    }

    [AvaloniaFact]
    public void The_os_reduced_motion_setting_forces_every_animator_to_the_reduced_variant()
    {
        var settings = Settings(MotionLevel.Liquid);
        settings.OsReducedMotion = true;
        var driver = new ManualFrameDriver();
        var button = new Border { Width = 80, Height = 32 };
        var animator = new PressAnimator(button, new Border(), settings, driver);

        animator.Press();
        var frames = Run(driver, animator);

        Assert.All(frames, f => Assert.Equal(1, f.Across));
        Assert.Null(button.RenderTransform);
        Assert.True(frames.Count <= 8, "at most about 90 ms of frames");
    }
}
