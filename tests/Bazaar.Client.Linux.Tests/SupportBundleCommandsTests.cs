using System.IO.Compression;
using Bazaar.Client.Linux;

namespace Bazaar.Client.Linux.Tests;

public sealed class SupportBundleCommandsTests
{
    [Fact]
    public void Ordinary_arguments_are_not_a_headless_mode()
    {
        Assert.Null(SupportBundleCommands.TryRun(["--open", "x"], "unused"));
    }

    [Fact]
    public void Self_check_passes_in_a_writable_directory_and_writes_the_report()
    {
        var dir = Directory.CreateTempSubdirectory().FullName;
        try
        {
            var report = Path.Combine(dir, "r.json");

            Assert.Equal(0, SupportBundleCommands.TryRun(["--self-check", "--report", report], dir));
            Assert.Contains("bazaar.selfcheck/1", File.ReadAllText(report));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void Support_bundle_without_a_path_is_a_usage_error_and_with_a_path_writes_a_zip()
    {
        var dir = Directory.CreateTempSubdirectory().FullName;
        try
        {
            Assert.Equal(2, SupportBundleCommands.TryRun(["--support-bundle"], dir));

            var zip = Path.Combine(dir, "out", "b.zip");
            Assert.Equal(0, SupportBundleCommands.TryRun(["--support-bundle", zip], dir));
            using var archive = ZipFile.OpenRead(zip);
            Assert.Contains(archive.Entries, e => e.FullName == "client/info.txt");
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }
}
