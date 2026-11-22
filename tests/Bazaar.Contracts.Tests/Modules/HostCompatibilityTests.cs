using Bazaar.Contracts.Modules;

namespace Bazaar.Contracts.Tests.Modules;

public class HostCompatibilityTests
{
    private static HostRequirements Windows(params string[] features) =>
        new(1, ["windows"], ["x64"], features);

    private static HostFacts Facts(string os, string arch, params string[] features) =>
        new(os, arch, new HashSet<string>(features, StringComparer.Ordinal));

    [Fact]
    public void Matching_host_has_no_issues()
    {
        var issues = HostCompatibility.Check(Windows("hyperv"), Facts("windows", "x64", "hyperv"));

        Assert.Empty(issues);
    }

    [Fact]
    public void Linux_host_is_refused_for_a_windows_only_module()
    {
        var issue = Assert.Single(HostCompatibility.Check(Windows(), Facts("linux", "x64")));

        Assert.Equal("os-unsupported", issue.Code);
    }

    [Fact]
    public void Wrong_architecture_is_refused()
    {
        var issue = Assert.Single(HostCompatibility.Check(Windows(), Facts("windows", "arm64")));

        Assert.Equal("arch-unsupported", issue.Code);
    }

    [Fact]
    public void Missing_required_feature_is_refused_and_named()
    {
        var issue = Assert.Single(HostCompatibility.Check(Windows("windows-sandbox"), Facts("windows", "x64")));

        Assert.Equal("feature-missing", issue.Code);
        Assert.Contains("windows-sandbox", issue.Message);
    }

    [Fact]
    public void Unknown_schema_version_is_reported_and_never_matches()
    {
        var issues = HostCompatibility.Check(new HostRequirements(2, ["windows"], ["x64"], []), Facts("windows", "x64"));

        Assert.Equal("schema-version", Assert.Single(issues).Code);
    }

    [Fact]
    public void Empty_os_or_arch_list_is_invalid_because_it_would_match_nothing()
    {
        var issues = HostCompatibility.ValidateRequirements(new HostRequirements(1, [], [], []));

        Assert.Contains(issues, i => i.Code == "empty-list" && i.Message.Contains("operatingSystems"));
        Assert.Contains(issues, i => i.Code == "empty-list" && i.Message.Contains("architectures"));
    }

    [Theory]
    [InlineData("darwin")]
    [InlineData("Windows")]
    [InlineData("")]
    public void Unknown_operating_system_names_are_rejected(string os)
    {
        var issues = HostCompatibility.ValidateRequirements(new HostRequirements(1, [os], ["x64"], []));

        Assert.Equal("unknown-value", Assert.Single(issues).Code);
    }

    [Fact]
    public void Duplicate_values_are_rejected()
    {
        var issues = HostCompatibility.ValidateRequirements(new HostRequirements(1, ["windows", "windows"], ["x64"], []));

        Assert.Equal("duplicate", Assert.Single(issues).Code);
    }

    [Fact]
    public void Null_requirements_are_reported_as_missing()
    {
        Assert.Equal("missing-field", Assert.Single(HostCompatibility.ValidateRequirements(null)).Code);
    }

    [Fact]
    public void Manifest_without_host_requirements_is_invalid()
    {
        var manifest = new ModuleManifest(
            "bazaar.gc", "0.1.0", 1, ["bazaar.gc"], ["bazaar.gc.open"], ["gc.density"], [], ModuleIsolation.InProcessCompiled, null!);

        var result = ModuleManifestValidator.Validate(manifest);

        Assert.False(result.IsValid);
        Assert.Contains(result.Issues, i => i.Code == "missing-field");
    }

    [Fact]
    public void Manifest_with_bad_host_requirements_names_the_host_path()
    {
        var manifest = new ModuleManifest(
            "bazaar.gc", "0.1.0", 1, ["bazaar.gc"], ["bazaar.gc.open"], ["gc.density"], [], ModuleIsolation.InProcessCompiled,
            new HostRequirements(1, ["plan9"], ["x64"], []));

        var result = ModuleManifestValidator.Validate(manifest);

        Assert.Contains(result.Issues, i => i.Path.StartsWith("/host", StringComparison.Ordinal) && i.Code == "unknown-value");
    }
}
