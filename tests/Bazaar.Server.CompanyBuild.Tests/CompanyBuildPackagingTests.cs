using Bazaar.Server.CompanyBuild;
using Xunit;

namespace Bazaar.Server.CompanyBuild.Tests;

public sealed class CompanyBuildPackagingTests
{
    [Fact]
    public void Windows_hosts_list_the_windows_packages_only() =>
        Assert.Equal(
            [CompanyBuildPackaging.VelopackId, CompanyBuildPackaging.MsixId],
            CompanyBuildPackaging.Targets("/src", isWindows: true, isLinux: false).Select(t => t.Id));

    [Fact]
    public void Linux_hosts_list_the_flatpak_only() =>
        Assert.Equal(
            [CompanyBuildPackaging.FlatpakId],
            CompanyBuildPackaging.Targets("/src", isWindows: false, isLinux: true).Select(t => t.Id));

    [Fact]
    public void Other_hosts_list_none() =>
        Assert.Empty(CompanyBuildPackaging.Targets("/src", isWindows: false, isLinux: false));

    [Fact]
    public void Velopack_arguments_name_the_script_and_every_input()
    {
        var args = CompanyBuildPackaging.VelopackArguments("/src", "0.1.0", "beta", "/tmp/pub", "/out/CDAX/windows-velopack");

        Assert.Equal(Path.Combine("/src", "packaging", "velopack", "pack-client.ps1"), args[args.ToList().IndexOf("-File") + 1]);
        Assert.Equal("0.1.0", args[args.ToList().IndexOf("-Version") + 1]);
        Assert.Equal("beta", args[args.ToList().IndexOf("-Channel") + 1]);
        Assert.Equal("/tmp/pub", args[args.ToList().IndexOf("-PublishDir") + 1]);
        Assert.Equal("/out/CDAX/windows-velopack", args[args.ToList().IndexOf("-OutputDir") + 1]);
    }

    [Fact]
    public void Msix_and_flatpak_arguments_carry_the_output_folder()
    {
        var msix = CompanyBuildPackaging.MsixArguments("/src", "Release", "/out/m");
        Assert.Equal("/out/m", msix[msix.ToList().IndexOf("-OutDir") + 1]);
        Assert.EndsWith("build-msix.ps1", msix[msix.ToList().IndexOf("-File") + 1]);

        var flatpak = CompanyBuildPackaging.FlatpakArguments("/src", "/out/f");
        Assert.EndsWith("build-flatpak.sh", flatpak[0]);
        Assert.Equal("/out/f", flatpak[^1]);
    }

    [Fact]
    public void A_blank_input_is_refused() =>
        Assert.ThrowsAny<ArgumentException>(() => CompanyBuildPackaging.VelopackArguments("/src", " ", "beta", "/p", "/o"));

    [Fact]
    public async Task A_failing_program_is_reported_with_its_exit_code()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "Needs bash, which is not guaranteed on a Windows host.");
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            ProcessRunner.RunAsync("bash", ["-c", "echo boom >&2; exit 7"], null, TestContext.Current.CancellationToken));

        Assert.Contains("exited with code 7", error.Message);
        Assert.Contains("boom", error.Message);
    }
}
