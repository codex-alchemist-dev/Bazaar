namespace Bazaar.Contracts.Tests;

public class ProductVersionTests
{
    [Theory]
    [InlineData(0, "Beta")]
    [InlineData(1, "Zuhal")]
    public void Known_majors_have_codenames(int major, string expected) =>
        Assert.Equal(expected, ProductVersion.Codename(major));

    [Fact]
    public void Unshipped_major_has_no_codename() => Assert.Null(ProductVersion.Codename(99));

    [Theory]
    [InlineData("0.1.0", 0)]
    [InlineData("v1.2.3", 1)]
    [InlineData("1.3.0-beta.2+a1b2c3d", 1)]
    [InlineData("12", 12)]
    public void Major_is_parsed_from_semver(string version, int expected) =>
        Assert.Equal(expected, ProductVersion.Major(version));

    [Fact]
    public void Running_build_carries_the_repo_version_prefix()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Directory.Build.props"))) dir = dir.Parent;
        Assert.NotNull(dir);
        var match = System.Text.RegularExpressions.Regex.Match(
            File.ReadAllText(Path.Combine(dir.FullName, "Directory.Build.props")), "<VersionPrefix>([^<]+)</VersionPrefix>");
        Assert.True(match.Success);
        Assert.StartsWith(match.Groups[1].Value, ProductVersion.Informational, StringComparison.Ordinal);
    }

    [Fact]
    public void Pre_release_builds_show_the_beta_codename() => Assert.Equal("Beta", ProductVersion.CurrentCodename);
}
