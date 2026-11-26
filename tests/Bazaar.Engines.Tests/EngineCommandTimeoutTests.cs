using Bazaar.Engines.Adapters;

namespace Bazaar.Engines.Tests;

public class EngineCommandTimeoutTests
{
    [Fact]
    public void Default_is_fifteen_minutes()
    {
        Assert.Equal(TimeSpan.FromMinutes(15), EngineCommandTimeout.Default);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(120)]
    public void Accepts_the_edges_of_the_range(int minutes)
    {
        Assert.Equal(TimeSpan.FromMinutes(minutes), EngineCommandTimeout.FromMinutes(minutes));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(121)]
    [InlineData(-5)]
    public void Rejects_values_outside_the_range(int minutes)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => EngineCommandTimeout.FromMinutes(minutes));
    }
}
