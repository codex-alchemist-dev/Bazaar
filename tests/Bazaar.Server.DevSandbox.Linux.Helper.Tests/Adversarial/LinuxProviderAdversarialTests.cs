using Bazaar.Server.DevSandbox.Linux;

namespace Bazaar.Server.DevSandbox.Linux.Helper.Tests.Adversarial;

[System.Runtime.Versioning.SupportedOSPlatform("linux")]
public sealed class LinuxProviderAdversarialTests
{
    private sealed class Plan : ILinuxSandboxLaunchPlan
    {
        public LinuxLaunchCommand Create(SandboxRunSpec spec, string workDirectory) => new("/bin/sh", ["-c", "true"]);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void The_launch_bounds_file_size_and_core_dumps_in_both_modes(bool hardened)
    {
        var options = new LinuxConfinementOptions { MaxFileSizeBytes = 1234 };
        var hardening = hardened ? new LinuxHardeningLaunch("/h/helper", "/w/policy.json") : null;

        var args = LinuxCommandLine.BuildArguments(options, "/usr/bin/unshare", 128, 30, new LinuxLaunchCommand("/bin/true", []), hardening);

        Assert.Contains("--fsize=1234", args);
        Assert.Contains("--core=0", args);
        Assert.Contains(args, a => a.StartsWith("--nproc=", StringComparison.Ordinal));
        Assert.Contains(args, a => a.StartsWith("--nofile=", StringComparison.Ordinal));
        Assert.Contains(args, a => a.StartsWith("--cpu=", StringComparison.Ordinal));
        Assert.Equal(!hardened, args.Any(a => a.StartsWith("--as=", StringComparison.Ordinal)));
    }

    [Fact]
    public void The_confined_command_is_never_built_through_a_shell_string()
    {
        var args = LinuxCommandLine.BuildArguments(
            new LinuxConfinementOptions(), "/usr/bin/unshare", 128, 30, new LinuxLaunchCommand("/bin/true", ["; rm -rf /", "$(id)", "`id`"]), null);

        Assert.Contains("; rm -rf /", args);
        Assert.Contains("$(id)", args);
        Assert.DoesNotContain(args, a => a.Contains("sh -c", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("run-1\n")]
    [InlineData("run-1\nx")]
    [InlineData("../run")]
    [InlineData("RUN")]
    [InlineData("run/1")]
    [InlineData("")]
    public async Task A_run_id_with_a_newline_or_path_characters_is_refused_before_a_work_directory_is_made(string runId)
    {
        var root = Directory.CreateTempSubdirectory("bazaar-adv-").FullName;
        try
        {
            var provider = new LinuxConfinementProvider(new LinuxConfinementOptions { WorkRoot = root }, new Plan());
            Assert.SkipUnless(provider.Tested, "unprivileged namespaces unavailable: " + provider.UntestedReason);
            var spec = new SandboxRunSpec(runId, "mod", "1.0.0", SandboxEngineKind.Vm, [], [], 1UL, TimeoutSeconds: 30, MemoryLimitMb: 128);

            await Assert.ThrowsAnyAsync<ArgumentException>(() => provider.StartAsync(spec, TestContext.Current.CancellationToken));

            Assert.Empty(Directory.GetFileSystemEntries(root));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }
}
