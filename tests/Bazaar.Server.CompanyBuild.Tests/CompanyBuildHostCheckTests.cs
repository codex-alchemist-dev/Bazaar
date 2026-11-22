using Bazaar.Contracts.Modules;
using Bazaar.Server.CompanyBuild;
using Bazaar.Server.CompanyBuild.Abstractions;
using Xunit;

namespace Bazaar.Server.CompanyBuild.Tests;

public sealed class CompanyBuildHostCheckTests
{
    private static ModuleManifest Module(string id, params string[] operatingSystems) => new(
        id,
        "0.1.0",
        1,
        [],
        [],
        [],
        [],
        ModuleIsolation.InProcessCompiled,
        new HostRequirements(HostRequirements.CurrentSchemaVersion, operatingSystems, ["x64", "arm64"], []));

    [Theory]
    [InlineData("win-x64", "windows", "x64")]
    [InlineData("linux-arm64", "linux", "arm64")]
    public void Facts_follow_the_runtime_identifier(string rid, string os, string architecture)
    {
        var facts = CompanyBuildHostCheck.FactsFor(new CompanyBuildTarget("t", "a.csproj", rid));

        Assert.Equal((os, architecture), (facts?.OperatingSystem, facts?.Architecture));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("osx-x64")]
    [InlineData("win")]
    public void Unknown_or_missing_identifiers_give_no_facts(string? rid) =>
        Assert.Null(CompanyBuildHostCheck.FactsFor(new CompanyBuildTarget("t", "a.csproj", rid)));

    [Fact]
    public void A_module_the_target_cannot_host_is_listed()
    {
        var modules = new[] { Module("gc", "windows", "linux"), Module("engines", "windows") };

        var unsupported = CompanyBuildHostCheck.Unsupported(modules, new CompanyBuildTarget("linux-x64", "a.csproj", "linux-x64"));

        Assert.Equal(["engines (os-unsupported)"], unsupported);
    }

    [Fact]
    public void A_target_without_facts_lists_nothing() =>
        Assert.Empty(CompanyBuildHostCheck.Unsupported([Module("engines", "windows")], new CompanyBuildTarget("t", "a.csproj")));

    [Fact]
    public void The_readme_names_modules_a_target_will_not_load()
    {
        var plan = new CompanyBuildPlan("acme", [], []);
        var report = new CompanyBuildSetReport("acme", DateTimeOffset.UnixEpoch, [], true, false, "out");
        var notes = new Dictionary<string, IReadOnlyList<string>> { ["linux-x64"] = ["engines (os-unsupported)"] };

        var readme = CompanyBuildFiles.RenderReadme(plan, report, notes);

        Assert.Contains("Client modules that will not load on linux-x64: engines (os-unsupported)", readme, StringComparison.Ordinal);
    }
}
