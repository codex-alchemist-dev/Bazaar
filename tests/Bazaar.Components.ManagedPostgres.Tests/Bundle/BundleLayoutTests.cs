using Bazaar.Components.ManagedPostgres.Bundle;

namespace Bazaar.Components.ManagedPostgres.Tests.Bundle;

public sealed class BundleLayoutTests : IDisposable
{
    private readonly string _temp = Path.Combine(Path.GetTempPath(), "bazaar-p4-layout-" + Guid.NewGuid().ToString("N"));

    public BundleLayoutTests() => Directory.CreateDirectory(_temp);

    public void Dispose()
    {
        if (Directory.Exists(_temp))
        {
            Directory.Delete(_temp, recursive: true);
        }
    }

    [Theory]
    [InlineData(18)]
    [InlineData(19)]
    public void Resolve_returns_bin_lib_share_and_data_for_each_supported_major(int major)
    {
        var root = Path.Combine(_temp, "pg");
        var dataRoot = Path.Combine(_temp, "data");
        var layout = new BundleLayout(root, dataRoot);

        var paths = layout.Resolve(major);

        var majorRoot = Path.Combine(root, major.ToString(System.Globalization.CultureInfo.InvariantCulture));
        Assert.Equal(major, paths.Major);
        Assert.Equal(Path.Combine(majorRoot, "bin"), paths.Bin);
        Assert.Equal(Path.Combine(majorRoot, "lib"), paths.Lib);
        Assert.Equal(Path.Combine(majorRoot, "share"), paths.Share);
        Assert.Equal(Path.Combine(dataRoot, "pg", major.ToString(System.Globalization.CultureInfo.InvariantCulture), "data"), paths.DataDirectory);
    }

    [Fact]
    public void Resolve_rejects_unsupported_major()
    {
        var layout = new BundleLayout(Path.Combine(_temp, "pg"), Path.Combine(_temp, "data"));

        Assert.Throws<ArgumentOutOfRangeException>(() => layout.Resolve(17));
    }

    [Fact]
    public void Default_major_is_a_supported_major()
    {
        Assert.Contains(BundleLayout.DefaultMajor, BundleLayout.SupportedMajors);
    }

    [Fact]
    public void PresentMajors_is_empty_when_nothing_is_installed()
    {
        var layout = new BundleLayout(Path.Combine(_temp, "pg"), Path.Combine(_temp, "data"));

        Assert.Empty(layout.PresentMajors());
    }

    [Fact]
    public void PresentMajors_needs_the_pgvector_control_file()
    {
        var root = Path.Combine(_temp, "pg");
        var layout = new BundleLayout(root, Path.Combine(_temp, "data"));
        var paths = layout.Resolve(18);
        Directory.CreateDirectory(paths.Bin);
        File.WriteAllText(Path.Combine(paths.Bin, "postgres"), string.Empty);

        Assert.Empty(layout.PresentMajors());

        Directory.CreateDirectory(Path.Combine(paths.Share, "extension"));
        File.WriteAllText(Path.Combine(paths.Share, "extension", "vector.control"), "default_version = '0.8.0'");

        Assert.Equal([18], layout.PresentMajors());
    }

    [Fact]
    public void PresentMajors_accepts_the_windows_executable_name_and_lists_both_majors()
    {
        var root = Path.Combine(_temp, "pg");
        var layout = new BundleLayout(root, Path.Combine(_temp, "data"));
        foreach (var major in BundleLayout.SupportedMajors)
        {
            var paths = layout.Resolve(major);
            Directory.CreateDirectory(paths.Bin);
            File.WriteAllText(Path.Combine(paths.Bin, "postgres.exe"), string.Empty);
            Directory.CreateDirectory(Path.Combine(paths.Share, "extension"));
            File.WriteAllText(Path.Combine(paths.Share, "extension", "vector.control"), "default_version = '0.8.0'");
        }

        Assert.Equal([18, 19], layout.PresentMajors());
    }

    [Fact]
    public void PresentMajors_skips_a_major_without_its_server_binary()
    {
        var layout = new BundleLayout(Path.Combine(_temp, "pg"), Path.Combine(_temp, "data"));
        var paths = layout.Resolve(19);
        Directory.CreateDirectory(Path.Combine(paths.Share, "extension"));
        File.WriteAllText(Path.Combine(paths.Share, "extension", "vector.control"), "default_version = '0.8.0'");

        Assert.Empty(layout.PresentMajors());
    }
}
