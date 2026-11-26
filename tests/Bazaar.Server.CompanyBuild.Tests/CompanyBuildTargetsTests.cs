using Bazaar.Server.CompanyBuild;
using Xunit;

namespace Bazaar.Server.CompanyBuild.Tests;

public sealed class CompanyBuildTargetsTests
{
    [Fact]
    public void Desktop_lists_windows_and_linux_with_runtime_identifiers()
    {
        var targets = CompanyBuildTargets.Desktop("/repo");

        Assert.Equal(["windows-x64", "linux-x64"], targets.Select(t => t.Id));
        Assert.Equal(["win-x64", "linux-x64"], targets.Select(t => t.RuntimeIdentifier));
        Assert.All(targets, t => Assert.EndsWith(".csproj", t.ProjectPath, StringComparison.Ordinal));
    }

    [Fact]
    public void Publish_arguments_add_a_self_contained_runtime_only_when_a_rid_is_given()
    {
        var withRid = DotnetPublishBuildStep.BuildArguments("a.csproj", "out", "Release", "win-x64");
        var without = DotnetPublishBuildStep.BuildArguments("a.csproj", "out");

        Assert.Equal(["-r", "win-x64", "--self-contained", "true"], withRid.Skip(7));
        Assert.Equal(7, without.Count);
    }
}
