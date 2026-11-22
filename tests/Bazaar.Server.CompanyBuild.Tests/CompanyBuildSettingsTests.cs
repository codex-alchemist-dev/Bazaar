using Bazaar.Server.CompanyBuild;
using Bazaar.Server.CompanyBuild.Settings;
using Xunit;

namespace Bazaar.Server.CompanyBuild.Tests;

public sealed class CompanyBuildSettingsTests
{
    [Fact]
    public void Default_is_the_runner_default() => Assert.Equal(CompanyBuildRunner.DefaultMaxAttempts, CompanyBuildSettings.MaxAttempts.Default);

    [Fact]
    public void Contribution_lists_the_key() =>
        Assert.Same(CompanyBuildSettings.MaxAttempts, Assert.Single(new CompanyBuildSettingContribution().Settings));

    [Theory]
    [InlineData(0)]
    [InlineData(11)]
    [InlineData(-1)]
    public void Out_of_range_is_rejected(int value) => Assert.NotNull(CompanyBuildSettings.MaxAttempts.CheckValue(System.Text.Json.JsonSerializer.SerializeToElement(value)));

    [Theory]
    [InlineData(1)]
    [InlineData(10)]
    public void Bounds_are_accepted(int value) => Assert.Null(CompanyBuildSettings.MaxAttempts.CheckValue(System.Text.Json.JsonSerializer.SerializeToElement(value)));
}
