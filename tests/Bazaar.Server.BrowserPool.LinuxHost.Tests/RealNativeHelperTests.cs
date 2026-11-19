using System.Diagnostics;
using System.Runtime.Versioning;
using System.Text;
using Bazaar.Server.BrowserPool.Abstractions;
using Bazaar.Server.BrowserPool.LinuxHost;

namespace Bazaar.Server.BrowserPool.LinuxHost.Tests;

/// <summary>
/// The same launch path as <see cref="RealPipeTests"/>, but through the native AOT build of <c>bazaar-pool-spawn</c>. Skipped unless the
/// real-tool gate is open (BAZAAR_TEST_REAL_NETNS=1, Linux, root) and BAZAAR_TEST_POOL_SPAWN_NATIVE names the published binary
/// (dotnet publish src/Bazaar.Guest.PoolSpawn -c Release -r linux-x64).
/// </summary>
[Collection("linuxhost-real")]
[SupportedOSPlatform("linux")]
public sealed class RealNativeHelperTests : IDisposable
{
    private const string _table = "bazaar_pool_nativetest";
    private static readonly TimeSpan _bound = TimeSpan.FromSeconds(60);

    private readonly string _unique = Guid.NewGuid().ToString("N")[..12];
    private bool _touchedTable;

    public void Dispose()
    {
        if (_touchedTable)
        {
            using var p = Process.Start(new ProcessStartInfo("/usr/sbin/nft") { ArgumentList = { "delete", "table", "inet", _table }, RedirectStandardError = true });
            p?.WaitForExit(30000);
        }

        try
        {
            Directory.Delete("/run/bazaar-pool-native-" + _unique, true);
        }
        catch (DirectoryNotFoundException)
        {
        }
    }

    /// <summary>The native binary's path, or null (with <paramref name="reason"/>) when the test must skip. Checked before any options are built.</summary>
    private static string? NativePath(out string reason)
    {
        reason = string.Empty;
        if (!OperatingSystem.IsLinux())
        {
            reason = "Not Linux.";
            return null;
        }

        if (Environment.GetEnvironmentVariable("BAZAAR_TEST_REAL_NETNS") != "1")
        {
            reason = "Set BAZAAR_TEST_REAL_NETNS=1 to run the tests that use real Linux tools.";
            return null;
        }

        var native = Environment.GetEnvironmentVariable("BAZAAR_TEST_POOL_SPAWN_NATIVE");
        if (string.IsNullOrEmpty(native) || !File.Exists(native))
        {
            reason = "Set BAZAAR_TEST_POOL_SPAWN_NATIVE to the published native bazaar-pool-spawn binary.";
            return null;
        }

        if (!File.ReadAllText("/proc/self/status").Contains("Uid:\t0\t", StringComparison.Ordinal))
        {
            reason = "Not running as root.";
            return null;
        }

        if (!File.Exists("/usr/sbin/nft") || !File.Exists("/usr/bin/dd"))
        {
            reason = "nft or dd is not installed.";
            return null;
        }

        return Path.GetFullPath(native);
    }

    private static string Render(IReadOnlyList<PoolFirewallRule> rules)
    {
        var accepts = string.Concat(rules.Select(r => $"    meta skuid {r.Uid} ip daddr {r.ProxyHost} tcp dport {r.ProxyPort} accept\n"));
        return $"table inet {_table}\ndelete table inet {_table}\ntable inet {_table} {{\n  chain output {{\n    type filter hook output priority 0; policy accept;\n"
            + accepts
            + "    meta skuid >= 58000 meta skuid <= 58010 drop\n  }\n"
            + "  chain input {\n    type filter hook input priority 0; policy accept;\n    iifname \"bzv*\" drop\n  }\n"
            + "  chain forward {\n    type filter hook forward priority 0; policy accept;\n    iifname \"bzv*\" drop\n  }\n}\n";
    }

    [Fact]
    public async Task The_native_helper_places_the_fifos_on_3_and_4_and_a_message_comes_back_through_the_host()
    {
        var helper = NativePath(out var reason);
        Assert.SkipWhen(helper is null, reason);
        Assert.SkipWhen(!PoolPipePlan.IsSafeAbsolutePath(helper!), "The native binary path has characters the host option refuses: " + helper);

        var ct = TestContext.Current.CancellationToken;
        var pipeDir = "/run/bazaar-pool-native-" + _unique + "/pipes";
        var host = new LinuxPoolVmHost(Render, new LinuxPoolHostOptions { TableName = _table, NftPath = "/usr/sbin/nft", FirewallTimeout = _bound, PoolSpawnHelperPath = helper, PipeDirectory = pipeDir });
        _touchedTable = true;
        var rule = new PoolFirewallRule(58001, "127.0.0.1", 3128);
        await host.ApplyFirewallAsync([rule], ct);

        // dd copies one read from descriptor 3 to descriptor 4, so a reply proves the native helper wired both FIFOs before execve.
        var spec = new PoolLaunchSpec("native" + _unique, "agent-a", 1, rule.Uid, [], ["/usr/bin/dd", "if=/proc/self/fd/3", "of=/proc/self/fd/4", "bs=65536", "count=1"], [], rule);
        var process = await host.StartAsync(spec, ct);
        Assert.True(host.TryGetPipe(spec.SessionId, out var pipe));

        var message = Encoding.UTF8.GetBytes("{\"id\":1,\"method\":\"Browser.getVersion\"}");
        using var writer = new DevToolsMessageWriter(pipe.ToBrowser);
        var reader = new DevToolsMessageReader(pipe.FromBrowser);
        await writer.WriteAsync(message, ct);

        Assert.Equal(message, await reader.ReadAsync(ct).AsTask().WaitAsync(_bound, ct));
        await host.StopAsync(process, ct);
        Assert.False(host.TryGetPipe(spec.SessionId, out _));
    }
}
