using System.IO.Compression;
using Bazaar.Client.Windows;

namespace Bazaar.Client.Windows.Tests;

public sealed class SupportBundleCommandsTests
{
    [Fact]
    public void Self_check_reports_both_start_up_checks_and_passes_on_windows()
    {
        var report = SupportBundleCommands.RunSelfCheck(WindowsModules.Create());

        Assert.Equal(["modules.load", "panes.registry"], report.Checks.Select(c => c.Id).ToArray());
        if (OperatingSystem.IsWindows())
        {
            Assert.True(report.Passed, string.Join("; ", report.Checks.Where(c => !c.Passed).Select(c => $"{c.Id}: {c.Reason}")));
        }
    }

    [Fact]
    public void Ordinary_arguments_are_not_a_headless_mode()
    {
        Assert.Null(SupportBundleCommands.TryRun(["--panes"], () => throw new InvalidOperationException("must not load modules")));
    }

    [Fact]
    public void Support_bundle_without_a_path_is_a_usage_error()
    {
        Assert.Equal(2, SupportBundleCommands.TryRun(["--support-bundle"], WindowsModules.Create));
    }

    [Fact]
    public void Support_bundle_writes_a_zip_with_the_module_list_and_the_manifest()
    {
        var dir = Directory.CreateTempSubdirectory().FullName;
        try
        {
            var zipPath = Path.Combine(dir, "out", "bundle.zip");
            SupportBundleCommands.WriteBundle(zipPath, dir, WindowsModules.Create());

            using var zip = ZipFile.OpenRead(zipPath);
            var names = zip.Entries.Select(e => e.FullName).ToArray();
            Assert.Contains("manifest.json", names);
            Assert.Contains("client/info.txt", names);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }
}
