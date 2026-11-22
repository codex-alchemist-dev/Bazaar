namespace Bazaar.Client.Motion.Tests;

public class InteractionPresetsTests
{
    [Fact]
    public void Every_catalogue_name_has_a_preset_with_three_levels()
    {
        foreach (var name in InteractionNames.All)
        {
            Assert.StartsWith("motion.interaction.", name);
            Assert.True(InteractionPresets.Default.TryGet(name, out var preset), name);
            Assert.True(preset.Enabled);
            foreach (var level in Enum.GetValues<MotionLevel>())
            {
                Assert.NotNull(InteractionPresets.Default.Resolve(name, level));
            }
        }
    }

    [Fact]
    public void Reduced_durations_are_floored_at_90_ms_even_when_a_document_asks_for_more()
    {
        var slow = new InteractionVariant(TimeSpan.FromSeconds(2), 0.3);
        var presets = InteractionPresets.Default.With(new InteractionPreset(InteractionNames.Ripple, slow, slow, slow));

        Assert.Equal(TimeSpan.FromSeconds(2), presets.Resolve(InteractionNames.Ripple, MotionLevel.Liquid)!.Duration);
        Assert.Equal(TimeSpan.FromMilliseconds(90), presets.Resolve(InteractionNames.Ripple, MotionLevel.Reduced)!.Duration);
    }

    [Fact]
    public void A_replaced_preset_changes_the_plan_and_leaves_the_defaults_alone()
    {
        var quick = new InteractionVariant(TimeSpan.FromMilliseconds(100), 0.5);
        var presets = InteractionPresets.Default.With(new InteractionPreset(InteractionNames.Ripple, quick, quick, quick));

        Assert.Equal(TimeSpan.FromMilliseconds(100), new RipplePlan(MotionLevel.Liquid, presets).TotalDuration);
        Assert.Equal(TimeSpan.FromMilliseconds(900), new RipplePlan(MotionLevel.Liquid).TotalDuration);
    }

    [Fact]
    public void A_removed_or_disabled_preset_does_nothing()
    {
        var removed = InteractionPresets.Default.Without(InteractionNames.Toggle);
        var plan = new TogglePlan(MotionLevel.Liquid, 0, 0, true, removed);
        Assert.Equal(TimeSpan.Zero, plan.TotalDuration);
        Assert.Equal(1, plan.Evaluate(TimeSpan.Zero).Progress);

        Assert.True(InteractionPresets.Default.TryGet(InteractionNames.Press, out var press));
        var disabled = InteractionPresets.Default.With(press with { Enabled = false });
        Assert.Null(disabled.Resolve(InteractionNames.Press, MotionLevel.Liquid));
        Assert.Equal(InteractionFrame.Rest, new PressPlan(MotionLevel.Liquid, 0, 0, true, disabled).Evaluate(TimeSpan.Zero));
    }
}

public class ValueTransitionTests
{
    [Theory]
    [InlineData(MotionLevel.Liquid)]
    [InlineData(MotionLevel.Subtle)]
    [InlineData(MotionLevel.Reduced)]
    public void Every_level_starts_at_the_start_and_ends_exactly_on_the_target(MotionLevel level)
    {
        var variant = InteractionPresets.Default.Resolve(InteractionNames.SendFill, level);
        var transition = new ValueTransition(level, variant, 0.25, 0, 1);

        Assert.Equal(0.25, transition.At(TimeSpan.Zero).Value, 9);
        Assert.Equal((1.0, 0.0), transition.At(transition.TotalDuration));
    }
}

public class RipplePlanTests
{
    [Fact]
    public void Liquid_ring_grows_to_full_size_and_fades_over_900_ms()
    {
        var plan = new RipplePlan(MotionLevel.Liquid);
        Assert.Equal(TimeSpan.FromMilliseconds(900), plan.TotalDuration);

        var early = plan.Evaluate(TimeSpan.FromMilliseconds(100));
        var late = plan.Evaluate(TimeSpan.FromMilliseconds(800));
        Assert.True(late.Scale > early.Scale);
        Assert.True(late.Opacity < early.Opacity);
        Assert.True(early.Opacity > 0);
        Assert.Equal(RipplePlan.Hidden, plan.Evaluate(plan.TotalDuration));
    }

    [Fact]
    public void Subtle_is_a_200_ms_tint_with_no_ring()
    {
        var plan = new RipplePlan(MotionLevel.Subtle);
        Assert.Equal(TimeSpan.FromMilliseconds(200), plan.TotalDuration);

        var frames = Enumerable.Range(0, 200).Select(ms => plan.Evaluate(TimeSpan.FromMilliseconds(ms))).ToArray();
        Assert.All(frames, f => Assert.Equal(0, f.Opacity));
        Assert.Contains(frames, f => f.Tint > 0.1);
        Assert.Equal(0, frames[0].Tint);
    }

    [Fact]
    public void Reduced_is_a_flash_of_at_most_90_ms_with_no_travel()
    {
        var plan = new RipplePlan(MotionLevel.Reduced);
        Assert.True(plan.TotalDuration <= TimeSpan.FromMilliseconds(90));

        var mid = plan.Evaluate(TimeSpan.FromMilliseconds(40));
        Assert.True(mid.Tint > 0);
        Assert.Equal(1, mid.Scale);
        Assert.Equal(0, mid.Tx);
    }
}

public class TogglePlanTests
{
    [Fact]
    public void Liquid_knob_stretches_while_travelling_keeps_volume_and_wobbles_once()
    {
        var plan = new TogglePlan(MotionLevel.Liquid, 0, 0, true);
        var frames = Enumerable.Range(0, 361).Select(ms => plan.Evaluate(TimeSpan.FromMilliseconds(ms))).ToArray();

        Assert.Contains(frames, f => f.Along > 1.05);
        Assert.All(frames, f => Assert.Equal(1.0, f.Along * f.Across, 9));
        Assert.Contains(frames, f => f.Progress > 1.0); // the wobble past the end stop
        Assert.True(frames.Max(f => f.Progress) < 1.2);
        Assert.Equal(1, frames[^1].Progress);
        Assert.Equal(1, frames[^1].Along);
    }

    [Fact]
    public void Track_tint_follows_the_knob_and_stays_in_range()
    {
        var plan = new TogglePlan(MotionLevel.Liquid, 0, 0, true);
        var frames = Enumerable.Range(0, 361).Select(ms => plan.Evaluate(TimeSpan.FromMilliseconds(ms))).ToArray();

        Assert.All(frames, f => Assert.InRange(f.Tint, 0, 1));
        Assert.Equal(0, frames[0].Tint);
        Assert.Equal(1, frames[^1].Tint);
    }

    [Fact]
    public void Subtle_slides_in_140_ms_without_stretch_or_overshoot()
    {
        var plan = new TogglePlan(MotionLevel.Subtle, 0, 0, true);
        Assert.Equal(TimeSpan.FromMilliseconds(140), plan.TotalDuration);

        var frames = Enumerable.Range(0, 141).Select(ms => plan.Evaluate(TimeSpan.FromMilliseconds(ms))).ToArray();
        Assert.All(frames, f => Assert.Equal(1, f.Along));
        Assert.All(frames, f => Assert.InRange(f.Progress, 0, 1));
    }

    [Fact]
    public void Reduced_is_instant()
    {
        var plan = new TogglePlan(MotionLevel.Reduced, 0, 0, true);

        Assert.Equal(TimeSpan.Zero, plan.TotalDuration);
        Assert.Equal(1, plan.Evaluate(TimeSpan.Zero).Progress);
    }

    [Fact]
    public void Flipping_mid_flight_continues_from_the_current_position_and_velocity()
    {
        var first = new TogglePlan(MotionLevel.Liquid, 0, 0, true);
        var at = TimeSpan.FromMilliseconds(80);
        var (position, velocity) = first.StateAt(at);

        var back = new TogglePlan(MotionLevel.Liquid, position, velocity, false);

        Assert.Equal(first.Evaluate(at).Progress, back.Evaluate(TimeSpan.Zero).Progress, 9);
        Assert.Equal(0, back.Evaluate(back.TotalDuration).Progress);
    }
}

public class PressPlanTests
{
    [Fact]
    public void Liquid_press_squashes_to_the_preset_scale_with_volume_kept()
    {
        var plan = new PressPlan(MotionLevel.Liquid, 0, 0, true);
        var end = plan.Evaluate(plan.TotalDuration);

        Assert.Equal(0.96, end.Across, 9);
        Assert.Equal(1.0, end.Along * end.Across, 9);
        Assert.Equal(1, end.Scale);
    }

    [Fact]
    public void Liquid_release_springs_back_past_rest_then_settles()
    {
        var plan = new PressPlan(MotionLevel.Liquid, 1, 0, false);
        var frames = Enumerable.Range(0, 281).Select(ms => plan.Evaluate(TimeSpan.FromMilliseconds(ms))).ToArray();

        Assert.Contains(frames, f => f.Across > 1.0);
        Assert.Equal(InteractionFrame.Rest, frames[^1] with { Progress = 0 });
    }

    [Theory]
    [InlineData(MotionLevel.Subtle)]
    [InlineData(MotionLevel.Reduced)]
    public void Other_levels_darken_without_moving(MotionLevel level)
    {
        var down = new PressPlan(level, 0, 0, true);
        var end = down.Evaluate(down.TotalDuration);

        Assert.Equal(0.12, end.Tint, 9);
        Assert.Equal(1, end.Along);
        Assert.Equal(1, end.Across);
        Assert.Equal(0, new PressPlan(level, 1, 0, false).Evaluate(TimeSpan.FromMilliseconds(500)).Tint);
    }

    [Fact]
    public void Reduced_never_takes_longer_than_90_ms()
    {
        Assert.True(new PressPlan(MotionLevel.Reduced, 0, 0, true).TotalDuration <= TimeSpan.FromMilliseconds(90));
        Assert.True(new PressPlan(MotionLevel.Reduced, 1, 0, false).TotalDuration <= TimeSpan.FromMilliseconds(90));
    }
}

public class SendButtonPlanTests
{
    [Fact]
    public void Fill_goes_from_empty_to_full_and_back_without_a_jump()
    {
        var arm = new SendFillPlan(MotionLevel.Liquid, 0, 0, true);
        Assert.Equal(0, arm.Evaluate(TimeSpan.Zero).Progress);
        Assert.Equal(1, arm.Evaluate(arm.TotalDuration).Progress);

        var (value, velocity) = arm.StateAt(TimeSpan.FromMilliseconds(100));
        var disarm = new SendFillPlan(MotionLevel.Liquid, value, velocity, false);
        Assert.Equal(value, disarm.Evaluate(TimeSpan.Zero).Progress, 9);
        Assert.Equal(0, disarm.Evaluate(disarm.TotalDuration).Progress);
    }

    [Fact]
    public void Subtle_fill_fades_to_the_accent()
    {
        var arm = new SendFillPlan(MotionLevel.Subtle, 0, 0, true);

        Assert.Equal(1, arm.Evaluate(arm.TotalDuration).Tint);
        Assert.InRange(arm.Evaluate(TimeSpan.FromMilliseconds(80)).Tint, 0.1, 0.99);
    }

    [Fact]
    public void Liquid_launch_squashes_rises_stretched_and_re_forms_at_rest()
    {
        var plan = new SendLaunchPlan(MotionLevel.Liquid);
        var frames = Enumerable.Range(0, 421).Select(ms => plan.Evaluate(TimeSpan.FromMilliseconds(ms))).ToArray();

        Assert.Contains(frames, f => f.Across < 0.95); // squash: the button gets shorter...
        Assert.Contains(frames, f => f.Ty < -10 && f.Across > 1.05); // ...then the drop rises stretched
        Assert.Contains(frames, f => f.Opacity < 0.1); // and is gone before it re-forms
        Assert.Contains(frames, f => f.Scale > 1.0); // re-forming overshoots once
        Assert.All(frames, f => Assert.Equal(1.0, f.Along * f.Across, 9));
        Assert.Equal(InteractionFrame.Rest, frames[^1]);
    }

    [Theory]
    [InlineData(MotionLevel.Subtle, 160)]
    [InlineData(MotionLevel.Reduced, 90)]
    public void Other_levels_pulse_a_tint_without_travel(MotionLevel level, int ms)
    {
        var plan = new SendLaunchPlan(level);
        Assert.Equal(TimeSpan.FromMilliseconds(ms), plan.TotalDuration);

        var frames = Enumerable.Range(0, ms).Select(t => plan.Evaluate(TimeSpan.FromMilliseconds(t))).ToArray();
        Assert.All(frames, f => Assert.Equal(0, f.Ty));
        Assert.All(frames, f => Assert.Equal(1, f.Scale));
        Assert.Contains(frames, f => f.Tint > 0.05);
        Assert.Equal(InteractionFrame.Rest, plan.Evaluate(plan.TotalDuration));
    }
}

public class ReactionPopPlanTests
{
    [Fact]
    public void Liquid_chip_leaves_the_message_edge_small_and_settles_with_one_overshoot()
    {
        var plan = new ReactionPopPlan(MotionLevel.Liquid, -20, 10);
        var start = plan.Evaluate(TimeSpan.Zero);
        var frames = Enumerable.Range(0, 321).Select(ms => plan.Evaluate(TimeSpan.FromMilliseconds(ms))).ToArray();

        Assert.Equal(-20, start.Tx, 9);
        Assert.Equal(10, start.Ty, 9);
        Assert.Equal(0.3, start.Scale, 9);
        Assert.Equal(0, start.Opacity);
        Assert.Contains(frames, f => f.Scale > 1.0);
        Assert.True(frames.Max(f => f.Scale) < 1.2);
        Assert.Equal(InteractionFrame.Rest, frames[^1]);
    }

    [Theory]
    [InlineData(MotionLevel.Subtle, 160)]
    [InlineData(MotionLevel.Reduced, 90)]
    public void Other_levels_only_fade_in_place(MotionLevel level, int ms)
    {
        var plan = new ReactionPopPlan(level, -20, 10);
        Assert.Equal(TimeSpan.FromMilliseconds(ms), plan.TotalDuration);

        var frames = Enumerable.Range(0, ms).Select(t => plan.Evaluate(TimeSpan.FromMilliseconds(t))).ToArray();
        Assert.All(frames, f => Assert.Equal(0, f.Tx));
        Assert.All(frames, f => Assert.Equal(1, f.Scale));
        Assert.Equal(0, frames[0].Opacity);
        Assert.All(frames, f => Assert.InRange(f.Opacity, 0, 1));
    }
}

public class BadgeBumpPlanTests
{
    [Fact]
    public void Liquid_appear_pops_in_from_nothing_with_one_overshoot()
    {
        var plan = new BadgeBumpPlan(MotionLevel.Liquid, BadgeChange.Appear);
        var frames = Enumerable.Range(0, 361).Select(ms => plan.Evaluate(TimeSpan.FromMilliseconds(ms))).ToArray();

        Assert.Equal(0, frames[0].Scale);
        Assert.Contains(frames, f => f.Scale > 1.0);
        Assert.Equal(InteractionFrame.Rest, frames[^1]);
    }

    [Fact]
    public void Liquid_count_change_swells_once_and_settles_back()
    {
        var plan = new BadgeBumpPlan(MotionLevel.Liquid, BadgeChange.CountChange);
        var frames = Enumerable.Range(0, 361).Select(ms => plan.Evaluate(TimeSpan.FromMilliseconds(ms))).ToArray();

        Assert.Equal(1, frames[0].Scale);
        Assert.Contains(frames, f => f.Scale > 1.05);
        Assert.Equal(InteractionFrame.Rest, frames[^1]);
    }

    [Theory]
    [InlineData(MotionLevel.Subtle, BadgeChange.Appear)]
    [InlineData(MotionLevel.Subtle, BadgeChange.CountChange)]
    [InlineData(MotionLevel.Reduced, BadgeChange.Appear)]
    [InlineData(MotionLevel.Reduced, BadgeChange.CountChange)]
    public void Other_levels_fade_without_scaling(MotionLevel level, BadgeChange change)
    {
        var plan = new BadgeBumpPlan(level, change);
        var frames = Enumerable.Range(0, (int)plan.TotalDuration.TotalMilliseconds).Select(t => plan.Evaluate(TimeSpan.FromMilliseconds(t))).ToArray();

        Assert.All(frames, f => Assert.Equal(1, f.Scale));
        Assert.All(frames, f => Assert.InRange(f.Opacity, 0, 1));
        Assert.Contains(frames, f => f.Opacity < 1);
        Assert.Equal(InteractionFrame.Rest, plan.Evaluate(plan.TotalDuration));
        Assert.True(level != MotionLevel.Reduced || plan.TotalDuration <= TimeSpan.FromMilliseconds(90));
    }
}
