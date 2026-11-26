using Avalonia.Controls;
using Avalonia.Headless.XUnit;

namespace Bazaar.Client.Linux.Tests;

public class FirstRunScreensTests
{
    [Theory]
    [InlineData(new string[0], false, false)]
    [InlineData(new[] { "--sign-in" }, true, false)]
    [InlineData(new[] { "--create-company" }, false, true)]
    [InlineData(new[] { "--sign-in", "--create-company" }, true, true)]
    [InlineData(new[] { "--first-run" }, true, true)]
    [InlineData(new[] { "--other" }, false, false)]
    public void Parse_reads_the_switches(string[] args, bool signIn, bool company)
    {
        Assert.Equal((signIn, company), FirstRunScreens.Parse(args));
    }

    [Fact]
    public void TryBuild_returns_null_without_switches()
    {
        Assert.Null(FirstRunScreens.TryBuild([]));
    }

    [AvaloniaFact]
    public void TryBuild_builds_both_screens_for_first_run()
    {
        var control = FirstRunScreens.TryBuild([FirstRunScreens.FirstRunSwitch]);

        var panel = Assert.IsType<StackPanel>(Assert.IsType<ScrollViewer>(control).Content);
        Assert.Equal(2, panel.Children.Count);
        // The walkthrough card, the composer and profile hosts, the sign-in screen and the create-company screen.
        Assert.Equal(5, panel.Children.Count);
    }

    [AvaloniaFact]
    public void TryBuild_builds_one_screen_for_one_switch()
    {
        var control = FirstRunScreens.TryBuild([FirstRunScreens.SignInSwitch]);

        var panel = Assert.IsType<StackPanel>(Assert.IsType<ScrollViewer>(control).Content);
        Assert.Single(panel.Children);
    }
}
