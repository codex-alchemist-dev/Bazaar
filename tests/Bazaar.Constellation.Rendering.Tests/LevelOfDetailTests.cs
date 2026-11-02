namespace Bazaar.Constellation.Rendering.Tests;

public class LevelOfDetailTests
{
    [Theory]
    [InlineData(0.1f, LodTier.Far)]
    [InlineData(0.25f, LodTier.Mid)]
    [InlineData(1.0f, LodTier.Mid)]
    [InlineData(1.5f, LodTier.Near)]
    [InlineData(8f, LodTier.Near)]
    public void Maps_zoom_to_default_tiers(float zoom, LodTier expected) =>
        Assert.Equal(expected, LevelOfDetail.TierFor(zoom));

    [Fact]
    public void Rejects_inverted_thresholds()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => LevelOfDetail.TierFor(1f, farBelow: 2f, nearAtOrAbove: 1f));
    }
}
