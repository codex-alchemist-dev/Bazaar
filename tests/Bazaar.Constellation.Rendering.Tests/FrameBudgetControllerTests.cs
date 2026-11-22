using Bazaar.Constellation.Rendering.Core;
using Xunit;

namespace Bazaar.Constellation.Rendering.Tests;

public sealed class FrameBudgetControllerTests
{
    [Fact]
    public void OverBudget_LowersDetailAndCaps()
    {
        var c = new FrameBudgetController(RenderTiers.Mid, 3.3);
        ViewSelectorOptions first = c.Update(5.0);
        for (int i = 0; i < 20; i++)
        {
            c.Update(5.0);
        }

        Assert.True(c.Detail < 0.2f);
        Assert.True(c.Current().MaxInstances < first.MaxInstances);
        Assert.True(c.Current().RefineAtPixels > first.RefineAtPixels);
        Assert.True(c.Detail >= FrameBudgetController.MinDetail);
    }

    [Fact]
    public void UnderBudget_RecoversSlowlyThenSpendsHeadroomOnQuality()
    {
        var c = new FrameBudgetController(RenderTiers.Mid, 3.3);
        for (int i = 0; i < 20; i++)
        {
            c.Update(5.0);
        }

        float low = c.Detail;
        for (int i = 0; i < 40; i++)
        {
            c.Update(1.0);
        }

        // Window still holds slow samples at first, then calm frames are counted before any rise.
        Assert.True(c.Detail <= low * 1.1f);
        for (int i = 0; i < 5000; i++)
        {
            c.Update(1.0);
        }

        Assert.Equal(FrameBudgetController.MaxDetail, c.Detail);
    }

    [Fact]
    public void OnTarget_HoldsSteady()
    {
        var c = new FrameBudgetController(RenderTiers.Mid, 3.3);
        for (int i = 0; i < 500; i++)
        {
            c.Update(3.0);
        }

        Assert.Equal(1f, c.Detail);
    }

    [Theory]
    [InlineData(0.2, "high")]
    [InlineData(1.0, "mid")]
    [InlineData(5.0, "phone")]
    [InlineData(0.0, "phone")]
    public void TierPick_FollowsMicroBenchmark(double msPerMillion, string expected) =>
        Assert.Equal(expected, RenderTiers.Pick(msPerMillion).Name);

    [Fact]
    public void RefreshRateTarget()
    {
        Assert.Equal(1000.0 / 144, FrameBudgetController.TargetForRefreshRate(144), 6);
    }
}
