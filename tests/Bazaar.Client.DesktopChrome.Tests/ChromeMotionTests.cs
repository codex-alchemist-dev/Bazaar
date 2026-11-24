using Bazaar.Client.Motion;

namespace Bazaar.Client.DesktopChrome.Tests;

public class ChromeMotionTests
{
    private static ChromeMotion Motion(MotionLevel? level, bool osReduced = false)
    {
        var rig = new ChromeRig(level, osReduced);
        return new ChromeMotion(new ChromeTheme(rig.Theme), rig.Settings);
    }

    [Fact]
    public void Subtle_scales_the_sheet_duration_by_the_desktop_token_and_leaves_tab_alone()
    {
        var theme = new ChromeTheme(new ChromeRig().Theme);
        ChromeMotion motion = Motion(null);

        Assert.Equal(MotionLevel.Subtle, motion.Level);
        Assert.Equal(TimeSpan.FromMilliseconds(theme.Number("motion.duration.sheet") * theme.Number("motion.scale.desktop")), motion.Sheet);
        Assert.Equal(theme.Duration("motion.duration.tab"), motion.Tab);
        Assert.True(motion.UsesSpring);
        Assert.True(motion.Travels);
    }

    [Fact]
    public void Liquid_on_desktop_keeps_the_full_sheet_duration()
    {
        var theme = new ChromeTheme(new ChromeRig().Theme);

        Assert.Equal(theme.Duration("motion.duration.sheet"), Motion(MotionLevel.Liquid).Sheet);
    }

    [Fact]
    public void Reduced_is_a_fade_of_at_most_90_ms_with_no_travel_and_no_spring()
    {
        ChromeMotion motion = Motion(MotionLevel.Reduced);

        Assert.True(motion.Sheet <= TimeSpan.FromMilliseconds(90));
        Assert.True(motion.Tab <= TimeSpan.FromMilliseconds(90));
        Assert.Equal(0, motion.Travel);
        Assert.False(motion.Travels);
        Assert.False(motion.UsesSpring);
    }

    [Fact]
    public void The_os_reduced_motion_flag_forces_reduced()
    {
        Assert.Equal(MotionLevel.Reduced, Motion(MotionLevel.Liquid, osReduced: true).Level);
    }

    [Fact]
    public void Travel_is_the_motion_token_for_the_subtle_slide()
    {
        var rig = new ChromeRig();

        Assert.Equal(rig.Settings.Tokens.SubtleSlide, new ChromeMotion(new ChromeTheme(rig.Theme), rig.Settings).Travel);
    }

    [Fact]
    public void The_spring_never_overshoots_and_runs_for_the_duration()
    {
        var frames = new ManualFrames();
        var animator = new ProgressAnimator(frames);
        double max = 0;
        animator.Changed += (_, _) => max = Math.Max(max, animator.Value);

        animator.AnimateTo(1, TimeSpan.FromMilliseconds(200), spring: true);
        TimeSpan took = frames.Settle();

        Assert.True(max <= 1.0);
        Assert.Equal(1, animator.Value);
        Assert.InRange(took.TotalMilliseconds, 180, 230);
    }

    [Fact]
    public void Retargeting_keeps_the_current_value()
    {
        var frames = new ManualFrames();
        var animator = new ProgressAnimator(frames);
        animator.AnimateTo(1, TimeSpan.FromMilliseconds(400), spring: true);
        for (int i = 0; i < 6; i++)
        {
            frames.Step();
        }

        double mid = animator.Value;
        animator.AnimateTo(0, TimeSpan.FromMilliseconds(400), spring: true);

        Assert.Equal(mid, animator.Value);
        frames.Settle();
        Assert.Equal(0, animator.Value);
    }

    [Fact]
    public void A_settled_animator_asks_for_no_frames()
    {
        var frames = new ManualFrames();
        var animator = new ProgressAnimator(frames);
        animator.AnimateTo(1, TimeSpan.FromMilliseconds(100), spring: false);
        frames.Settle();

        Assert.False(frames.HasPending);
        Assert.False(animator.IsRunning);
    }
}
