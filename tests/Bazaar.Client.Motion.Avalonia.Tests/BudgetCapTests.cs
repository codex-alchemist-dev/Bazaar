namespace Bazaar.Client.Motion.Avalonia.Tests;

public class BudgetCapTests
{
    [Theory]
    [InlineData(MotionLevel.Liquid, MotionLevel.Subtle, MotionLevel.Subtle)]
    [InlineData(MotionLevel.Liquid, MotionLevel.Reduced, MotionLevel.Reduced)]
    [InlineData(MotionLevel.Subtle, MotionLevel.Subtle, MotionLevel.Subtle)]
    [InlineData(MotionLevel.Reduced, MotionLevel.Subtle, MotionLevel.Reduced)]
    public void The_cap_only_ever_lowers_the_level(MotionLevel setting, MotionLevel cap, MotionLevel expected)
    {
        var settings = new MotionSettings(MotionPlatform.Phone) { Setting = setting, BudgetCap = cap };

        Assert.Equal(expected, settings.Effective);
    }

    [Fact]
    public void Clearing_the_cap_restores_the_person_s_level_and_both_changes_notify()
    {
        var settings = new MotionSettings(MotionPlatform.Phone) { Setting = MotionLevel.Liquid };
        int raised = 0;
        settings.Changed += (_, _) => raised++;

        settings.BudgetCap = MotionLevel.Reduced;
        Assert.Equal(MotionLevel.Reduced, settings.Effective);
        settings.BudgetCap = null;

        Assert.Equal(MotionLevel.Liquid, settings.Effective);
        Assert.Equal(2, raised);
    }
}
