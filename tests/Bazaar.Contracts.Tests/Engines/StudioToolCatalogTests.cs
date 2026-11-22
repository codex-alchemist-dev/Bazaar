using Bazaar.Contracts.Engines;

namespace Bazaar.Contracts.Tests.Engines;

public class StudioToolCatalogTests
{
    [Theory]
    [InlineData("screen_capture")]
    [InlineData("user_keyboard_input")]
    [InlineData("user_mouse_input")]
    [InlineData("character_navigation")]
    public void Screen_capture_and_input_tools_are_exec(string tool)
    {
        Assert.True(StudioToolCatalog.TryGetRiskClass(tool, out var riskClass));
        Assert.Equal(RiskClass.Exec, riskClass);
    }

    [Theory]
    [InlineData("unknown_tool")]
    [InlineData("screen_capture_v2")]
    [InlineData("")]
    [InlineData(null)]
    public void Unknown_tools_are_denied(string? tool)
    {
        Assert.False(StudioToolCatalog.TryGetRiskClass(tool, out _));
    }

    [Fact]
    public void Generate_prefix_is_write()
    {
        Assert.True(StudioToolCatalog.TryGetRiskClass("generate_mesh", out var riskClass));
        Assert.Equal(RiskClass.Write, riskClass);
    }

    [Fact]
    public void Bare_generate_prefix_is_denied()
    {
        Assert.False(StudioToolCatalog.TryGetRiskClass("generate_", out _));
    }
}
