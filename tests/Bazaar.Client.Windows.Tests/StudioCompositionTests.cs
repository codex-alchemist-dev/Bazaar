using Bazaar.Client.Windows;

namespace Bazaar.Client.Windows.Tests;

public sealed class StudioCompositionTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("  ")]
    [InlineData("auto")]
    [InlineData("AUTO")]
    public void Unset_or_auto_with_nothing_found_keeps_the_bridge_off(string? setting) =>
        Assert.Null(StudioComposition.ResolvePath(setting, _ => true));

    [Fact]
    public void Auto_uses_a_detected_path_only_when_the_file_exists()
    {
        Assert.Equal(@"C:\x\mcp.exe", StudioComposition.ResolvePath("auto", _ => true, () => @"C:\x\mcp.exe"));
        Assert.Null(StudioComposition.ResolvePath("auto", _ => false, () => @"C:\x\mcp.exe"));
    }

    [Fact]
    public void A_set_path_must_exist()
    {
        Assert.Equal(@"D:\mcp.exe", StudioComposition.ResolvePath(@" D:\mcp.exe ", p => p == @"D:\mcp.exe"));
        Assert.Null(StudioComposition.ResolvePath(@"D:\missing.exe", _ => false));
    }
}
