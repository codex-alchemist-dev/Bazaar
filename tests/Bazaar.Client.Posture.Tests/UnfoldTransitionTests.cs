using Bazaar.Client.Motion;

namespace Bazaar.Client.Posture.Tests;

public class UnfoldTransitionTests
{
    private static readonly TimeSpan _frame = TimeSpan.FromMilliseconds(16);

    private static void Run(UnfoldTransition t, TimeSpan total)
    {
        for (var e = TimeSpan.Zero; e < total; e += _frame)
        {
            t.Advance(_frame);
        }
    }

    [Fact]
    public void Tracking_follows_the_angle()
    {
        var t = new UnfoldTransition(MotionLevel.Liquid);
        t.Retarget(1);
        t.TrackAngle(95); // (95 - 20) / (170 - 20) = 0.5
        Assert.Equal(UnfoldMode.Tracking, t.Mode);
        Assert.Equal(0.5, t.Progress, 6);
        t.TrackAngle(10);
        Assert.Equal(0, t.Progress);
        t.TrackAngle(179);
        Assert.Equal(1, t.Progress);
    }

    [Fact]
    public void Tracking_settles_to_the_goal_when_the_hands_let_go()
    {
        var t = new UnfoldTransition(MotionLevel.Liquid);
        t.Retarget(1);
        t.TrackAngle(95);
        t.Advance(TimeSpan.FromMilliseconds(60));
        Assert.Equal(UnfoldMode.Tracking, t.Mode);
        t.Advance(TimeSpan.FromMilliseconds(100));
        Assert.Equal(UnfoldMode.Timed, t.Mode);
        Run(t, TimeSpan.FromMilliseconds(700));
        Assert.True(t.IsSettled);
        Assert.Equal(1, t.Progress);
    }

    [Fact]
    public void Timed_unfold_without_an_angle_is_a_spring_with_one_overshoot_at_liquid()
    {
        var t = new UnfoldTransition(MotionLevel.Liquid);
        t.Retarget(1);
        Assert.Equal(UnfoldMode.Timed, t.Mode);
        var max = 0.0;
        for (var e = TimeSpan.Zero; e < TimeSpan.FromMilliseconds(600); e += _frame)
        {
            max = Math.Max(max, t.Advance(_frame).Progress);
        }

        Assert.True(max > 1.0, "liquid spring overshoots once");
        Assert.True(max < 1.1);
        Assert.True(t.IsSettled);
        Assert.Equal(1, t.Progress);
    }

    [Fact]
    public void Subtle_is_faster_and_never_overshoots()
    {
        var t = new UnfoldTransition(MotionLevel.Subtle);
        t.Retarget(1);
        var max = 0.0;
        for (var e = TimeSpan.Zero; e < TimeSpan.FromMilliseconds(230); e += _frame)
        {
            max = Math.Max(max, t.Advance(_frame).Progress);
        }

        Assert.True(max <= 1.0000001);
        Assert.True(t.IsSettled);
    }

    [Fact]
    public void Fold_reverses_the_unfold()
    {
        var t = new UnfoldTransition(MotionLevel.Subtle, initialProgress: 1);
        t.Retarget(0);
        Run(t, TimeSpan.FromMilliseconds(300));
        Assert.True(t.IsSettled);
        Assert.Equal(0, t.Progress);
    }

    [Fact]
    public void Retarget_mid_flight_keeps_position_without_a_jump()
    {
        var t = new UnfoldTransition(MotionLevel.Subtle);
        t.Retarget(1);
        Run(t, TimeSpan.FromMilliseconds(80));
        var before = t.Progress;
        Assert.InRange(before, 0.05, 0.95);
        t.Retarget(0);
        Assert.Equal(before, t.Progress);
        var after = t.Advance(TimeSpan.FromMilliseconds(1)).Progress;
        Assert.InRange(Math.Abs(after - before), 0, 0.05);
    }

    [Fact]
    public void Reduced_is_a_cross_fade_of_at_most_90_ms_with_no_travel()
    {
        var t = new UnfoldTransition(MotionLevel.Reduced);
        t.Retarget(1);
        Assert.Equal(UnfoldMode.Fading, t.Mode);
        Assert.Equal(0, t.Progress); // still the old layout while it fades out
        var minOpacity = 1.0;
        var switchedAt = TimeSpan.Zero;
        var elapsed = TimeSpan.Zero;
        var step = TimeSpan.FromMilliseconds(5);
        while (!t.IsSettled)
        {
            var f = t.Advance(step);
            elapsed += step;
            minOpacity = Math.Min(minOpacity, f.ContentOpacity);
            if (switchedAt == TimeSpan.Zero && f.Progress == 1)
            {
                switchedAt = elapsed;
            }

            Assert.True(f.Progress is 0 or 1, "no intermediate layout progress, so no travel");
            Assert.Equal(0, f.NewPaneOffset);
            Assert.True(elapsed <= TimeSpan.FromMilliseconds(95));
        }

        Assert.True(elapsed <= TimeSpan.FromMilliseconds(95));
        Assert.True(minOpacity < 0.2);
        Assert.InRange(switchedAt.TotalMilliseconds, 40, 55);
        Assert.Equal(1, t.Current.ContentOpacity);
        Assert.Equal(1, t.Progress);
    }

    [Fact]
    public void Reduced_ignores_the_angle()
    {
        var t = new UnfoldTransition(MotionLevel.Reduced);
        t.TrackAngle(100);
        Assert.Equal(UnfoldMode.Idle, t.Mode);
        Assert.Equal(0, t.Progress);
    }

    [Fact]
    public void Reduced_fade_honours_the_ceiling_even_with_a_longer_token()
    {
        var tokens = MotionTokens.Default with { ReducedFade = TimeSpan.FromMilliseconds(500) };
        var t = new UnfoldTransition(MotionLevel.Reduced, motion: tokens);
        t.Retarget(1);
        Run(t, TimeSpan.FromMilliseconds(96));
        Assert.True(t.IsSettled);
    }

    [Fact]
    public void Level_change_rests_at_the_goal()
    {
        var t = new UnfoldTransition(MotionLevel.Liquid);
        t.Retarget(1);
        Run(t, TimeSpan.FromMilliseconds(100));
        t.SetLevel(MotionLevel.Reduced);
        Assert.True(t.IsSettled);
        Assert.Equal(1, t.Progress);
    }

    [Fact]
    public void Frame_channels_bud_the_pane_and_swap_strip_for_pager()
    {
        var start = UnfoldFrame.FromProgress(0);
        var end = UnfoldFrame.FromProgress(1);
        Assert.Equal(0, start.NewPaneOpacity);
        Assert.Equal(1, start.NewPaneOffset);
        Assert.Equal(1, start.ChatStripOpacity);
        Assert.Equal(0, start.PagerOpacity);
        Assert.Equal(1, end.NewPaneOpacity);
        Assert.Equal(0, end.NewPaneOffset);
        Assert.Equal(0, end.ChatStripOpacity);
        Assert.Equal(1, end.PagerOpacity);
    }
}

public class PostureTransitionControllerTests
{
    private static readonly TimeSpan _frame = TimeSpan.FromMilliseconds(16);
    private static readonly PostureState _book = new(DevicePosture.Book, null, new HingeInset(HingeOrientation.Vertical, 500, 20));
    private static readonly PostureState _flat = new(DevicePosture.Flat, null, new HingeInset(HingeOrientation.Vertical, 500, 20));

    [Fact]
    public void First_reading_snaps_with_no_animation()
    {
        var c = new PostureTransitionController(MotionLevel.Liquid);
        c.Update(_flat, 1000, LayoutVariants.All);
        Assert.False(c.IsRunning);
        Assert.Equal(PostureLayoutClass.DualPane, c.Current.Class);
        Assert.Equal(1, c.Current.Unfold.Progress);
    }

    [Fact]
    public void Unfold_without_an_angle_plays_a_timed_transition_into_the_new_class()
    {
        var c = new PostureTransitionController(MotionLevel.Subtle);
        c.Update(PostureState.Flat, 400, LayoutVariants.All);
        Assert.Equal(PostureLayoutClass.Folded, c.Current.Class);
        c.Update(_flat, 1000, LayoutVariants.All);
        Assert.True(c.IsRunning);
        Assert.Equal(PostureLayoutClass.DualPane, c.Current.Class);
        Assert.Equal(0, c.Current.Unfold.Progress);
        while (c.IsRunning)
        {
            c.Advance(_frame);
        }

        Assert.Equal(1, c.Current.Unfold.Progress);
    }

    [Fact]
    public void Fold_keeps_the_class_until_the_panes_have_shrunk_back()
    {
        var c = new PostureTransitionController(MotionLevel.Subtle);
        c.Update(_book, 1000, LayoutVariants.All);
        c.Update(PostureState.Flat, 400, LayoutVariants.All);
        Assert.True(c.IsRunning);
        Assert.Equal(PostureLayoutClass.Book, c.Current.Class);
        c.Advance(_frame);
        Assert.Equal(PostureLayoutClass.Book, c.Current.Class);
        while (c.IsRunning)
        {
            c.Advance(_frame);
        }

        Assert.Equal(PostureLayoutClass.Folded, c.Current.Class);
        Assert.Equal(0, c.Current.Unfold.Progress);
    }

    [Fact]
    public void With_an_angle_progress_follows_the_hinge()
    {
        var c = new PostureTransitionController(MotionLevel.Liquid);
        c.Update(PostureState.Flat, 400, LayoutVariants.All);
        c.Update(_book with { FoldAngleDegrees = 60 }, 1000, LayoutVariants.All);
        var p1 = c.Current.Unfold.Progress;
        c.Update(_book with { FoldAngleDegrees = 120 }, 1000, LayoutVariants.All);
        var p2 = c.Current.Unfold.Progress;
        Assert.Equal((60.0 - 20) / 150, p1, 6);
        Assert.Equal((120.0 - 20) / 150, p2, 6);
        Assert.Equal(PostureLayoutClass.Book, c.Current.Class);
    }

    [Fact]
    public void Same_angle_does_not_restart_tracking()
    {
        var c = new PostureTransitionController(MotionLevel.Liquid);
        c.Update(PostureState.Flat, 400, LayoutVariants.All);
        var s = _book with { FoldAngleDegrees = 60 };
        c.Update(s, 1000, LayoutVariants.All);
        c.Advance(TimeSpan.FromMilliseconds(200));
        c.Update(s, 1010, LayoutVariants.All); // a size change only
        c.Advance(_frame);
        Assert.True(c.Current.Unfold.Progress > (60.0 - 20) / 150);
    }

    [Fact]
    public void Reduced_level_cross_fades_on_posture_change()
    {
        var c = new PostureTransitionController(MotionLevel.Reduced);
        c.Update(PostureState.Flat, 400, LayoutVariants.All);
        c.Update(_book with { FoldAngleDegrees = 80 }, 1000, LayoutVariants.All);
        Assert.True(c.IsRunning);
        var elapsed = TimeSpan.Zero;
        while (c.IsRunning)
        {
            var f = c.Advance(TimeSpan.FromMilliseconds(5));
            elapsed += TimeSpan.FromMilliseconds(5);
            Assert.True(f.Unfold.Progress is 0 or 1);
        }

        Assert.True(elapsed <= TimeSpan.FromMilliseconds(95));
        Assert.Equal(PostureLayoutClass.Book, c.Current.Class);
    }

    [Fact]
    public void Fake_source_raises_changed_with_the_new_reading()
    {
        var source = new FakePostureSource();
        PostureState? seen = null;
        source.Changed += (_, s) => seen = s;
        source.Set(_book);
        Assert.Equal(_book, seen);
        Assert.Equal(_book, source.Current);
    }
}
