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

public sealed class SupportBundlePaletteTests
{
    [Fact]
    public void The_save_command_loads_and_its_mnemonic_fits_the_client_table_with_the_shipped_commands()
    {
        var loaded = Bazaar.Client.Modules.ClientModuleHost.Load([new Bazaar.SupportBundle.SupportBundleClientModule()], Bazaar.Client.Modules.ClientHostFacts.Current());

        if (OperatingSystem.IsWindows() || OperatingSystem.IsLinux())
        {
            Assert.Empty(loaded.Skipped);
            var command = Assert.Single(loaded.Commands);
            Assert.Equal("supportbundle.save", command.Id);
            Assert.NotEmpty(Bazaar.Ex.Head.MnemonicHeadSetup.Combine(loaded.Commands));
        }
    }

    [Fact]
    public void Saving_from_the_palette_writes_a_timestamped_zip_and_reports_the_path()
    {
        var dir = Directory.CreateTempSubdirectory().FullName;
        try
        {
            var lines = new List<string>();
            var path = Bazaar.Client.Windows.SupportBundleCommands.SaveFromPalette(Bazaar.Client.Windows.WindowsModules.Create(), dir, new Clock(), lines.Add);

            Assert.NotNull(path);
            Assert.EndsWith("bazaar-support-20261122-210000.zip", path);
            Assert.True(File.Exists(path));
            Assert.Contains(lines, l => l.Contains("support bundle saved"));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    private sealed class Clock : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => new(2026, 11, 22, 21, 0, 0, TimeSpan.Zero);
    }
}
