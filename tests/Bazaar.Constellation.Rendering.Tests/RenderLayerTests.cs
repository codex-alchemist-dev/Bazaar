using Bazaar.Constellation.Rendering.Core;

namespace Bazaar.Constellation.Rendering.Tests;

public class RenderLayerTests
{
    [Fact]
    public void Create_keeps_the_name_and_order()
    {
        RenderLayer layer = RenderLayer.Create("edges", 2);
        Assert.Equal(new RenderLayer("edges", 2), layer);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("\t")]
    public void Create_rejects_null_empty_and_whitespace_names(string? name)
    {
        Assert.Throws<ArgumentException>(() => RenderLayer.Create(name!, 0));
    }
}
