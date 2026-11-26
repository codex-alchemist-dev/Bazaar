namespace Bazaar.Accessibility.Tests;

public class MotionPolicyTests
{
    private static readonly TimeSpan _long = TimeSpan.FromMilliseconds(400);

    [Fact]
    public void Full_motion_keeps_the_requested_durations()
    {
        var policy = new MotionPolicy();

        Assert.Equal(_long, policy.Decorative(_long));
        Assert.Equal(_long, policy.Essential(_long));
        Assert.True(policy.AllowDecorativeLoops);
    }

    [Fact]
    public void Reduced_motion_makes_decorative_animation_instant_and_caps_essential()
    {
        var policy = new MotionPolicy(reduced: true);

        Assert.Equal(TimeSpan.Zero, policy.Decorative(_long));
        Assert.Equal(MotionPolicy.EssentialCap, policy.Essential(_long));
        Assert.Equal(TimeSpan.FromMilliseconds(50), policy.Essential(TimeSpan.FromMilliseconds(50)));
        Assert.False(policy.AllowDecorativeLoops);
    }

    [Fact]
    public void Negative_durations_become_zero()
    {
        var policy = new MotionPolicy();

        Assert.Equal(TimeSpan.Zero, policy.Decorative(TimeSpan.FromSeconds(-1)));
        Assert.Equal(TimeSpan.Zero, policy.Essential(TimeSpan.FromSeconds(-1)));
    }

    [Fact]
    public void Changed_is_raised_once_per_real_change()
    {
        var policy = new MotionPolicy();
        var raised = 0;
        policy.Changed += (_, _) => raised++;

        policy.Reduced = false;
        policy.Reduced = true;
        policy.Reduced = true;

        Assert.Equal(1, raised);
    }
}
