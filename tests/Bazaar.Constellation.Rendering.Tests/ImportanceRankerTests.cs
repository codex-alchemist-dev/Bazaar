using Xunit;

namespace Bazaar.Constellation.Rendering.Tests;

public sealed class ImportanceRankerTests
{
    [Fact]
    public void KeepsHighestScoresInIndexOrder()
    {
        float[] scores = [1f, 9f, 3f, 7f, 5f];
        Assert.Equal([1, 3], ImportanceRanker.SelectTop(scores, 2));
    }

    [Fact]
    public void TiesGoToLowerIndex()
    {
        float[] scores = [2f, 2f, 2f, 2f];
        Assert.Equal([0, 1], ImportanceRanker.SelectTop(scores, 2));
    }

    [Fact]
    public void KeepAtLeastCountReturnsAll()
    {
        Assert.Equal([0, 1, 2], ImportanceRanker.SelectTop(new float[] { 3f, 1f, 2f }, 10));
    }

    [Fact]
    public void ZeroKeepOrEmptyReturnsNothing()
    {
        Assert.Empty(ImportanceRanker.SelectTop(new float[] { 1f }, 0));
        Assert.Empty(ImportanceRanker.SelectTop([], 3));
    }

    [Fact]
    public void NaNRanksLast()
    {
        float[] scores = [float.NaN, 1f, 2f];
        Assert.Equal([1, 2], ImportanceRanker.SelectTop(scores, 2));
    }
}
