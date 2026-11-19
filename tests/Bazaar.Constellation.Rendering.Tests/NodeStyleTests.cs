using Bazaar.Constellation.Rendering.Core;

namespace Bazaar.Constellation.Rendering.Tests;

public class NodeStyleTests
{
    [Fact]
    public void Create_keeps_valid_components()
    {
        NodeStyle style = NodeStyle.Create(0.1f, 0.2f, 0.3f, 0.4f, 5f);
        Assert.Equal(new NodeStyle(0.1f, 0.2f, 0.3f, 0.4f, 5f), style);
    }

    [Fact]
    public void Create_accepts_the_boundary_values()
    {
        NodeStyle style = NodeStyle.Create(0f, 1f, 0f, 1f, 0f);
        Assert.Equal(0f, style.Radius);
        Assert.Equal(1f, style.Green);
    }

    [Theory]
    [InlineData(-0.1f)]
    [InlineData(1.1f)]
    [InlineData(float.NaN)]
    [InlineData(float.PositiveInfinity)]
    public void Create_rejects_each_colour_component_by_name(float value)
    {
        Assert.Equal("red", Assert.Throws<ArgumentOutOfRangeException>(() => NodeStyle.Create(value, 0f, 0f, 1f, 1f)).ParamName);
        Assert.Equal("green", Assert.Throws<ArgumentOutOfRangeException>(() => NodeStyle.Create(0f, value, 0f, 1f, 1f)).ParamName);
        Assert.Equal("blue", Assert.Throws<ArgumentOutOfRangeException>(() => NodeStyle.Create(0f, 0f, value, 1f, 1f)).ParamName);
        Assert.Equal("alpha", Assert.Throws<ArgumentOutOfRangeException>(() => NodeStyle.Create(0f, 0f, 0f, value, 1f)).ParamName);
    }

    [Theory]
    [InlineData(-1f)]
    [InlineData(float.NaN)]
    [InlineData(float.NegativeInfinity)]
    public void Create_rejects_a_bad_radius(float radius)
    {
        var error = Assert.Throws<ArgumentOutOfRangeException>(() => NodeStyle.Create(0f, 0f, 0f, 1f, radius));
        Assert.Equal("radius", error.ParamName);
    }
}
