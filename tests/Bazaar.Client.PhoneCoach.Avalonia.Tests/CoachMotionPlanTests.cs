using Bazaar.Client.Motion;

namespace Bazaar.Client.PhoneCoach.Avalonia.Tests;

public class CoachMotionPlanTests
{
    [Fact]
    public void Reduced_is_a_fade_of_at_most_90_ms_with_no_scale()
    {
        var plan = CoachMotionPlan.For(MotionLevel.Reduced, MotionTokens.Default);

        Assert.True(plan.Duration <= TimeSpan.FromMilliseconds(90));
        Assert.Equal(1, plan.StartScale);
    }

    [Fact]
    public void Reduced_stays_within_the_floor_even_when_tokens_ask_for_more()
    {
        var tokens = (MotionTokens.Default with { ReducedFade = TimeSpan.FromSeconds(1) }).Clamped();

        Assert.True(CoachMotionPlan.For(MotionLevel.Reduced, tokens).Duration <= TimeSpan.FromMilliseconds(90));
    }

    [Fact]
    public void Liquid_takes_longer_than_subtle_and_both_scale_in()
    {
        var liquid = CoachMotionPlan.For(MotionLevel.Liquid, MotionTokens.Default);
        var subtle = CoachMotionPlan.For(MotionLevel.Subtle, MotionTokens.Default);

        Assert.True(liquid.Duration > subtle.Duration);
        Assert.True(liquid.StartScale < 1);
        Assert.True(subtle.StartScale < 1);
    }
}
