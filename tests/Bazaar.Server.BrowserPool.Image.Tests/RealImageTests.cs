using Bazaar.Server.BrowserPool.LinuxHost;

namespace Bazaar.Server.BrowserPool.Image.Tests;

/// <summary>
/// The readiness check against the real file system. Skipped unless BAZAAR_TEST_REAL_NETNS=1, the OS is Linux and the process is
/// root (the gate of the pool's other real tests), and <c>bwrap</c> exists. It checks only that the real probes run without error
/// for a requirement set limited to what the machine has; it does not demand a complete pool image.
/// </summary>
public sealed class RealImageTests
{
    private static string? SkipReason()
    {
        if (!OperatingSystem.IsLinux())
        {
            return "Not Linux.";
        }

        if (Environment.GetEnvironmentVariable("BAZAAR_TEST_REAL_NETNS") != "1")
        {
            return "Set BAZAAR_TEST_REAL_NETNS=1 to run the tests that use real Linux tools.";
        }

        if (!File.ReadAllText("/proc/self/status").Contains("Uid:\t0\t", StringComparison.Ordinal))
        {
            return "Not running as root.";
        }

        return File.Exists("/usr/bin/bwrap") ? null : "/usr/bin/bwrap is not installed.";
    }

    [Fact]
    public async Task The_real_file_system_probes_work()
    {
        if (SkipReason() is { } reason)
        {
            Assert.Skip(reason);
        }

        var pool = new BrowserPoolOptions { SlotRoot = "/tmp/bazaar-image-test-" + Guid.NewGuid().ToString("N")[..8] };
        PoolImageRequirements all = PoolImageRequirements.From(pool, new LinuxPoolHostOptions());
        var fs = new PhysicalPoolImageFileSystem();
        var check = new PoolImageReadinessCheck(all, fs, new PoolImageOptions(), TimeProvider.System);

        var failures = await check.CheckAsync(CancellationToken.None);

        // The slot root does not exist, so that one must be reported; bwrap exists and must not be.
        Assert.Contains(failures, f => f.Id == "directory:slot-root");
        Assert.DoesNotContain(failures, f => f.Id == "program:bwrap" && f.Reason.Contains("not found", StringComparison.Ordinal));
        Assert.All(failures, f => Assert.False(string.IsNullOrWhiteSpace(f.Reason)));
        Assert.True(fs.DirectoryExists("/usr"));
        Assert.NotNull(fs.GetUnixMode("/usr/bin/bwrap"));
    }
}
