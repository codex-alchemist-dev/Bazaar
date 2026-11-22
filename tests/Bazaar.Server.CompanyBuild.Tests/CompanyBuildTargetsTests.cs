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

    [Fact]
    public void Android_target_publishes_an_apk_on_the_android_framework()
    {
        var target = CompanyBuildTargets.Android("/src");

        Assert.Equal("android-apk", target.Id);
        Assert.EndsWith("Bazaar.Client.Android.csproj", target.ProjectPath);
        Assert.Null(target.RuntimeIdentifier);

        var args = DotnetPublishBuildStep.BuildArguments(target.ProjectPath, "out", "Release", target.RuntimeIdentifier, target.ExtraArguments);
        Assert.Contains("-p:BazaarAndroid=true", args);
        Assert.Contains("-p:AndroidPackageFormats=apk", args);
        Assert.DoesNotContain("--self-contained", args);
        Assert.Equal("net11.0-android", args[args.ToList().IndexOf("-f") + 1]);
    }

    [Fact]
    public void Extra_arguments_are_appended_last_and_default_to_none()
    {
        Assert.Equal("--nologo", DotnetPublishBuildStep.BuildArguments("a.csproj", "out")[^1]);
        Assert.Equal("-x", DotnetPublishBuildStep.BuildArguments("a.csproj", "out", extraArguments: ["-x"])[^1]);
    }
}
