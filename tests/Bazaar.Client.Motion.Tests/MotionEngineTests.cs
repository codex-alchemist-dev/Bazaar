namespace Bazaar.Client.Motion.Tests;

public class SpringSolverTests
{
    [Fact]
    public void Under_damped_step_overshoots_once_and_settles_at_the_target()
    {
        var settle = TimeSpan.FromMilliseconds(200);
        var values = Enumerable.Range(0, 201).Select(ms => SpringSolver.StepResponse(0.7, settle, ms / 1000.0)).ToArray();

        Assert.True(values.Max() > 1.0, "expected an overshoot");
        Assert.True(values.Max() < 1.1, "overshoot stays small");
        Assert.Equal(1.0, values[^1], 2);

        // One overshoot: after the peak the curve falls back and any second swing stays under half a percent.
        var peak = Array.IndexOf(values, values.Max());
        Assert.All(values.Skip(peak + 1).SkipWhile(v => v > 1.0), v => Assert.True(v < 1.005));
    }

    [Fact]
    public void Critically_damped_step_never_overshoots()
    {
        var values = Enumerable.Range(0, 301).Select(ms => SpringSolver.StepResponse(1.0, TimeSpan.FromMilliseconds(300), ms / 1000.0));

        Assert.All(values, v => Assert.InRange(v, 0.0, 1.0 + 1e-9));
    }

    [Fact]
    public void Over_damped_step_is_monotonic()
    {
        var values = Enumerable.Range(0, 301).Select(ms => SpringSolver.StepResponse(1.6, TimeSpan.FromMilliseconds(300), ms / 1000.0)).ToArray();

        for (var i = 1; i < values.Length; i++)
        {
            Assert.True(values[i] >= values[i - 1] - 1e-9);
        }
    }

    [Theory]
    [InlineData(0.5)]
    [InlineData(1.0)]
    [InlineData(1.5)]
    public void A_retarget_keeps_position_and_velocity(double zeta)
    {
        var omega = SpringSolver.OmegaForSettle(zeta, TimeSpan.FromMilliseconds(300));
        var (x1, v1) = SpringSolver.Evaluate(0, 0, 1, zeta, omega, 0.05);

        // Restarting from the state at 50 ms toward the same target continues the same curve.
        var (xa, va) = SpringSolver.Evaluate(0, 0, 1, zeta, omega, 0.12);
        var (xb, vb) = SpringSolver.Evaluate(x1, v1, 1, zeta, omega, 0.07);

        Assert.Equal(xa, xb, 9);
        Assert.Equal(va, vb, 6);
    }
}

public class SquashStretchTests
{
    [Theory]
    [InlineData(-1.0)]
    [InlineData(-0.4)]
    [InlineData(0.0)]
    [InlineData(0.3)]
    [InlineData(1.0)]
    [InlineData(5.0)]
    public void Volume_is_kept(double amount)
    {
        var s = SquashStretch.FromAmount(amount, MotionTokens.Default);

        Assert.Equal(1.0, s.Along * s.Across, 9);
    }

    [Fact]
    public void Stays_within_the_token_limits()
    {
        var t = MotionTokens.Default;

        Assert.Equal(t.MaxStretch, SquashStretch.FromAmount(9, t).Along, 9);
        Assert.Equal(t.MaxSquash, SquashStretch.FromAmount(-9, t).Along, 9);
        Assert.Equal(1.0, SquashStretch.FromAmount(0, t).Along, 9);
    }
}

public class MotionLevelResolverTests
{
    [Theory]
    [InlineData(MotionPlatform.Phone, MotionLevel.Liquid)]
    [InlineData(MotionPlatform.Desktop, MotionLevel.Subtle)]
    public void Defaults_by_platform(MotionPlatform platform, MotionLevel expected) =>
        Assert.Equal(expected, MotionLevelResolver.Resolve(null, false, platform));

    [Theory]
    [InlineData(null)]
    [InlineData(MotionLevel.Liquid)]
    [InlineData(MotionLevel.Subtle)]
    public void The_os_reduced_motion_setting_forces_reduced(MotionLevel? setting) =>
        Assert.Equal(MotionLevel.Reduced, MotionLevelResolver.Resolve(setting, true, MotionPlatform.Phone));

    [Fact]
    public void The_person_choice_beats_the_platform_default() =>
        Assert.Equal(MotionLevel.Reduced, MotionLevelResolver.Resolve(MotionLevel.Reduced, false, MotionPlatform.Phone));

    [Theory]
    [InlineData("liquid", MotionLevel.Liquid)]
    [InlineData("Subtle", MotionLevel.Subtle)]
    [InlineData(" REDUCED ", MotionLevel.Reduced)]
    public void Parses_the_stored_value(string text, MotionLevel expected)
    {
        Assert.True(MotionLevelResolver.TryParse(text, out var level));
        Assert.Equal(expected, level);
        Assert.Equal(MotionLevelResolver.Format(level), MotionLevelResolver.Format(expected));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("fast")]
    [InlineData("7")]
    public void Rejects_unknown_values(string? text) => Assert.False(MotionLevelResolver.TryParse(text, out _));

    [Fact]
    public void The_setting_key_is_the_documented_one() => Assert.Equal("client.motion_level", MotionSettingKeys.MotionLevel);
}

public class MotionTokensTests
{
    [Fact]
    public void Defaults_match_doc_93()
    {
        var t = MotionTokens.Default;

        Assert.Equal(TimeSpan.FromMilliseconds(320), t.MessageArrival);
        Assert.Equal(TimeSpan.FromMilliseconds(160), t.SubtleDuration);
        Assert.Equal(TimeSpan.FromMilliseconds(90), t.ReducedFade);
        Assert.Equal(0.96, t.SubtleStartScale);
        Assert.Equal(8, t.SubtleSlide);
    }

    [Fact]
    public void The_reduced_fade_cannot_exceed_90_ms()
    {
        var t = new MotionTokens { ReducedFade = TimeSpan.FromMilliseconds(400) }.Clamped();

        Assert.Equal(TimeSpan.FromMilliseconds(90), t.ReducedFade);
    }
}

public class DropletPlanTests
{
    private static readonly TimeSpan _frame = TimeSpan.FromMilliseconds(4);

    private static IEnumerable<MotionFrame> Frames(DropletPlan plan)
    {
        for (var t = TimeSpan.Zero; t <= plan.TotalDuration + _frame; t += _frame)
        {
            yield return plan.Evaluate(t);
        }
    }

    [Theory]
    [InlineData(MotionLevel.Liquid, DropletKind.Arrival)]
    [InlineData(MotionLevel.Liquid, DropletKind.OwnSent)]
    [InlineData(MotionLevel.Subtle, DropletKind.Arrival)]
    [InlineData(MotionLevel.Subtle, DropletKind.OwnSent)]
    [InlineData(MotionLevel.Reduced, DropletKind.Arrival)]
    public void Every_level_ends_exactly_at_rest(MotionLevel level, DropletKind kind)
    {
        var plan = new DropletPlan(level, kind, 120, 300);

        Assert.Equal(MotionFrame.Rest, plan.Evaluate(plan.TotalDuration));
        Assert.Equal(MotionFrame.Rest, plan.Evaluate(plan.TotalDuration + TimeSpan.FromSeconds(5)));
    }

    [Fact]
    public void Liquid_starts_as_a_small_drop_at_the_origin()
    {
        var plan = new DropletPlan(MotionLevel.Liquid, DropletKind.Arrival, 120, 300);
        var first = plan.Evaluate(TimeSpan.Zero);

        Assert.Equal(120, first.Tx, 6);
        Assert.Equal(300, first.Ty, 6);
        Assert.Equal(MotionTokens.Default.DropletStartScale, first.Scale, 6);
        Assert.Equal(0, first.Opacity, 6);
        Assert.Equal(0, first.ContentOpacity);
    }

    [Fact]
    public void Liquid_drop_travels_toward_the_final_place()
    {
        var plan = new DropletPlan(MotionLevel.Liquid, DropletKind.Arrival, 0, 300, new MotionTokens { DropletArc = 0 });
        var travelEnd = TimeSpan.FromMilliseconds(320 * 0.4);
        var ys = Enumerable.Range(0, 33).Select(i => plan.Evaluate(travelEnd * i / 32.0).Ty).ToArray();

        for (var i = 1; i < ys.Length; i++)
        {
            Assert.True(ys[i] <= ys[i - 1] + 1e-9, "the drop only moves toward the final place");
        }

        Assert.Equal(0, ys[^1], 3);
    }

    [Fact]
    public void Liquid_keeps_volume_and_respects_the_stretch_limit_in_every_frame()
    {
        var plan = new DropletPlan(MotionLevel.Liquid, DropletKind.Arrival, 80, 250);
        var max = MotionTokens.Default.MaxStretch;

        foreach (var f in Frames(plan))
        {
            Assert.Equal(1.0, f.Along * f.Across, 9);
            Assert.InRange(f.Along, 1.0 - 1e-9, max + 1e-9);
        }

        Assert.Contains(Frames(plan), f => f.Along > 1.2);
    }

    [Fact]
    public void Liquid_expands_with_one_overshoot()
    {
        var plan = new DropletPlan(MotionLevel.Liquid, DropletKind.Arrival, 0, 200);
        var scales = Frames(plan).Select(f => f.Scale).ToArray();

        Assert.True(scales.Max() > 1.0, "the bubble overshoots its size");
        Assert.True(scales.Max() < 1.1);

        // It rises past 1 once and settles: after the peak any second swing stays under half a percent.
        var peak = Array.IndexOf(scales, scales.Max());
        Assert.All(scales.Skip(peak + 1).SkipWhile(v => v > 1.0), v => Assert.True(v < 1.005));
    }

    [Fact]
    public void Liquid_text_stays_hidden_until_the_shape_has_settled_then_fades_in()
    {
        var plan = new DropletPlan(MotionLevel.Liquid, DropletKind.Arrival, 40, 200);
        var shape = MotionTokens.Default.MessageArrival;

        Assert.All(Frames(plan).TakeWhile(f => f != MotionFrame.Rest && f.ContentOpacity == 0).Where(f => f.Scale > 1.0), f => Assert.Equal(0, f.ContentOpacity));
        Assert.Equal(0, plan.Evaluate(shape - TimeSpan.FromMilliseconds(1)).ContentOpacity);
        Assert.InRange(plan.Evaluate(shape + MotionTokens.Default.TextFadeIn / 2).ContentOpacity, 0.4, 0.6);
        Assert.Equal(1, plan.Evaluate(shape + MotionTokens.Default.TextFadeIn).ContentOpacity);
    }

    [Fact]
    public void Subtle_arrival_scales_from_point_96_and_fades_in_160_ms()
    {
        var plan = new DropletPlan(MotionLevel.Subtle, DropletKind.Arrival, 120, 300);
        var first = plan.Evaluate(TimeSpan.Zero);

        Assert.Equal(TimeSpan.FromMilliseconds(160), plan.TotalDuration);
        Assert.Equal(0.96, first.Scale, 9);
        Assert.Equal(0, first.Opacity, 9);
        Assert.All(Frames(plan), f =>
        {
            Assert.Equal(0, f.Tx);
            Assert.Equal(0, f.Ty);
            Assert.Equal(1, f.Along);
        });
    }

    [Fact]
    public void Subtle_own_message_slides_8_px_and_fades()
    {
        var plan = new DropletPlan(MotionLevel.Subtle, DropletKind.OwnSent, 120, 300);
        var first = plan.Evaluate(TimeSpan.Zero);

        Assert.Equal(8, first.Ty, 9);
        Assert.Equal(0, first.Tx);
        Assert.Equal(1, first.Scale);
    }

    [Fact]
    public void Reduced_is_a_fade_of_at_most_90_ms_with_no_travel_or_squash()
    {
        var plan = new DropletPlan(MotionLevel.Reduced, DropletKind.Arrival, 120, 300, new MotionTokens { ReducedFade = TimeSpan.FromSeconds(2) });

        Assert.True(plan.TotalDuration <= TimeSpan.FromMilliseconds(90));
        Assert.All(Frames(plan), f =>
        {
            Assert.Equal(0, f.Tx);
            Assert.Equal(0, f.Ty);
            Assert.Equal(1, f.Scale);
            Assert.Equal(1, f.Along);
            Assert.Equal(1, f.Across);
        });
        Assert.Equal(0, plan.Evaluate(TimeSpan.Zero).Opacity);
    }

    [Fact]
    public void Liquid_with_no_distance_has_no_stretch()
    {
        var plan = new DropletPlan(MotionLevel.Liquid, DropletKind.Arrival, 0, 0);

        Assert.All(Frames(plan), f => Assert.Equal(1.0, f.Along));
    }
}

public class DropletPlayerTests
{
    private static readonly TimeSpan _frame = TimeSpan.FromMilliseconds(8);

    [Fact]
    public void Runs_to_completion_and_ends_at_rest()
    {
        var player = new DropletPlayer(new DropletPlan(MotionLevel.Liquid, DropletKind.Arrival, 50, 200));

        for (var i = 0; i < 200 && !player.IsComplete; i++)
        {
            player.Advance(_frame);
        }

        Assert.True(player.IsComplete);
        Assert.Equal(MotionFrame.Rest, player.Current);
    }

    [Fact]
    public void An_interrupt_keeps_the_current_values_and_does_not_jump()
    {
        var player = new DropletPlayer(new DropletPlan(MotionLevel.Liquid, DropletKind.Arrival, 0, 300));
        for (var i = 0; i < 6; i++)
        {
            player.Advance(_frame);
        }

        var before = player.Current;
        player.Interrupt();

        Assert.True(player.IsSettling);
        Assert.Equal(before, player.Current);

        var next = player.Advance(TimeSpan.FromMilliseconds(1));
        Assert.True(Math.Abs(next.Ty - before.Ty) < 20, "no jump on the first frame after an interrupt");
    }

    [Fact]
    public void An_interrupt_keeps_the_direction_of_motion_then_settles_without_overshoot()
    {
        var player = new DropletPlayer(new DropletPlan(MotionLevel.Liquid, DropletKind.Arrival, 0, 300, new MotionTokens { DropletArc = 0 }));
        for (var i = 0; i < 6; i++)
        {
            player.Advance(_frame);
        }

        var ty0 = player.Current.Ty;
        player.Interrupt();

        var ys = new List<double> { ty0 };
        while (!player.IsComplete)
        {
            ys.Add(player.Advance(TimeSpan.FromMilliseconds(4)).Ty);
        }

        // Moving toward the origin (falling), so the first step continues downward in value, and it never crosses to the far side.
        Assert.True(ys[1] <= ys[0] + 1e-9);
        Assert.All(ys, y => Assert.True(y >= -1e-6));
        Assert.Equal(MotionFrame.Rest, player.Current);
    }

    [Fact]
    public void Interrupt_at_the_reduced_level_ends_at_once()
    {
        var player = new DropletPlayer(new DropletPlan(MotionLevel.Reduced, DropletKind.Arrival, 50, 200));
        player.Advance(TimeSpan.FromMilliseconds(30));

        player.Interrupt();

        Assert.True(player.IsComplete);
        Assert.Equal(MotionFrame.Rest, player.Current);
    }

    [Fact]
    public void A_settling_frame_keeps_opacity_in_range()
    {
        var player = new DropletPlayer(new DropletPlan(MotionLevel.Liquid, DropletKind.Arrival, 0, 300));
        player.Advance(TimeSpan.FromMilliseconds(20));
        player.Interrupt();

        while (!player.IsComplete)
        {
            var f = player.Advance(TimeSpan.FromMilliseconds(4));
            Assert.InRange(f.Opacity, 0, 1);
            Assert.InRange(f.ContentOpacity, 0, 1);
        }
    }
}

public class TypingIndicatorPlanTests
{
    private static IEnumerable<TypingDotFrame[]> Cycle(MotionLevel level) =>
        Enumerable.Range(0, 120).Select(i => TypingIndicatorPlan.Evaluate(level, TimeSpan.FromMilliseconds(i * 10), 12));

    [Fact]
    public void Liquid_drops_merge_and_separate_and_keep_volume()
    {
        var gaps = Cycle(MotionLevel.Liquid).Select(d => d[2].OffsetX - d[0].OffsetX).ToArray();

        Assert.True(gaps.Min() < gaps.Max() * 0.3, "the drops come together");
        Assert.Equal(24, gaps[0], 6); // apart at the start of the loop
        Assert.All(Cycle(MotionLevel.Liquid).SelectMany(d => d), f => Assert.Equal(1.0, f.ScaleX * f.ScaleY, 9));
    }

    [Fact]
    public void Subtle_dots_bounce_in_turn_without_sideways_motion()
    {
        var frames = Cycle(MotionLevel.Subtle).ToArray();

        Assert.All(frames.SelectMany(d => d), f => Assert.Equal(0, f.OffsetX));
        Assert.All(frames.SelectMany(d => d), f => Assert.InRange(f.OffsetY, -MotionTokens.Default.TypingBounce - 1e-9, 0));
        Assert.Contains(frames, d => d[0].OffsetY < -1 && d[2].OffsetY == 0);
    }

    [Fact]
    public void Reduced_dots_fade_in_turn_without_any_movement()
    {
        var frames = Cycle(MotionLevel.Reduced).ToArray();

        Assert.All(frames.SelectMany(d => d), f =>
        {
            Assert.Equal(0, f.OffsetX);
            Assert.Equal(0, f.OffsetY);
            Assert.Equal(1, f.ScaleX);
        });
        Assert.All(frames, d => Assert.Equal(1, d.Count(f => f.Opacity == 1.0)));
        Assert.Equal(3, frames.Select(d => Array.FindIndex(d, f => f.Opacity == 1.0)).Distinct().Count());
    }

    [Fact]
    public void The_loop_repeats()
    {
        var a = TypingIndicatorPlan.Evaluate(MotionLevel.Liquid, TimeSpan.FromMilliseconds(130), 12);
        var b = TypingIndicatorPlan.Evaluate(MotionLevel.Liquid, TimeSpan.FromMilliseconds(130 + 1200), 12);

        Assert.Equal(a, b);
    }
}

public class GooMergeEffectTests
{
    [Fact]
    public void The_default_is_a_no_op_that_reports_unavailable()
    {
        IGooMergeEffect goo = NoGooMergeEffect.Instance;

        Assert.False(goo.IsAvailable);
        Assert.False(goo.TryApply(new GooMergeRequest(0, 0, 1, 1, 0.5)));
    }
}
