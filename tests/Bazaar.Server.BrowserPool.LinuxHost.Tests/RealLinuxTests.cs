using System.Diagnostics;
using Bazaar.Server.BrowserPool.Abstractions;
using Bazaar.Server.BrowserPool.LinuxHost;

namespace Bazaar.Server.BrowserPool.LinuxHost.Tests;

/// <summary>
/// Runs real Linux tools (<c>nft</c>, <c>sh</c>, <c>sleep</c>, <c>cat</c>) as root. Skipped unless BAZAAR_TEST_REAL_NETNS=1, the OS
/// is Linux and the process is root (the pattern of the pool module's own real tests). The firewall tests use their own table and
/// a uid range far from any real slot, and remove the table again.
/// </summary>
[Collection("linuxhost-real")]
public sealed class RealLinuxTests : IDisposable
{
    private const string _table = "bazaar_pool_lhtest";
    private const int _first = 58000;
    private const int _last = 58010;

    private static readonly TimeSpan _bound = TimeSpan.FromSeconds(30);

    private readonly ProcessRunner _runner = new();

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

        foreach (var tool in new[] { "/bin/sh", "/usr/sbin/nft" })
        {
            if (!File.Exists(tool))
            {
                return tool + " is not installed.";
            }
        }

        return null;
    }

    private static string Render(IReadOnlyList<PoolFirewallRule> rules)
    {
        var accepts = string.Concat(rules.Select(r => $"    meta skuid {r.Uid} ip daddr {r.ProxyHost} tcp dport {r.ProxyPort} accept\n"));
        return $"table inet {_table}\ndelete table inet {_table}\ntable inet {_table} {{\n  chain output {{\n    type filter hook output priority 0; policy accept;\n"
            + accepts
            + $"    meta skuid >= {_first} meta skuid <= {_last} drop\n  }}\n}}\n";
    }

    private static LinuxPoolVmHost RealHost() =>
        new(Render, new LinuxPoolHostOptions { TableName = _table, NftPath = "/usr/sbin/nft", FirewallTimeout = _bound });

    private static ProcessRequest Req(string stdin, TimeSpan timeout, params string[] argv) => new(argv, stdin.Length == 0 ? null : stdin, timeout, 512);

    public void Dispose()
    {
        if (SkipReason() is not null)
        {
            return;
        }

        using var p = Process.Start(new ProcessStartInfo("/usr/sbin/nft", ["delete", "table", "inet", _table]) { RedirectStandardError = true, UseShellExecute = false });
        p?.WaitForExit();
    }

    [Fact]
    public async Task Arguments_reach_the_program_unparsed_and_stdin_is_delivered()
    {
        Assert.SkipWhen(SkipReason() is not null, SkipReason() ?? string.Empty);
        var ct = TestContext.Current.CancellationToken;

        var echo = await _runner.RunAsync(Req(string.Empty, _bound, "/bin/sh", "-c", "printf '%s' \"$1\"", "sh", "a b; $HOME `id` *"), ct);
        Assert.Equal("a b; $HOME `id` *", echo.StdOut);

        var cat = await _runner.RunAsync(Req("hello\n", _bound, "/bin/cat"), ct);
        Assert.Equal("hello\n", cat.StdOut);
    }

    [Fact]
    public async Task Stderr_and_stdout_are_bounded_and_the_exit_code_is_returned()
    {
        Assert.SkipWhen(SkipReason() is not null, SkipReason() ?? string.Empty);
        var ct = TestContext.Current.CancellationToken;

        var result = await _runner.RunAsync(
            Req(string.Empty, _bound, "/bin/sh", "-c", "i=0; while [ $i -lt 2000 ]; do echo xxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxx >&2; i=$((i+1)); done; exit 4"), ct);

        Assert.Equal(4, result.ExitCode);
        Assert.Equal(512, result.StdErr.Length);
        Assert.True(result.StdErrTruncated);
    }

    [Fact]
    public async Task A_timeout_kills_the_whole_tree()
    {
        Assert.SkipWhen(SkipReason() is not null, SkipReason() ?? string.Empty);
        var marker = "61." + Random.Shared.Next(100000, 999999);
        var sw = Stopwatch.StartNew();

        await Assert.ThrowsAsync<TimeoutException>(() => _runner.RunAsync(
            Req(string.Empty, TimeSpan.FromMilliseconds(500), "/bin/sh", "-c", "sleep " + marker + " & wait"), TestContext.Current.CancellationToken));

        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(20));
        await Task.Delay(300, TestContext.Current.CancellationToken);
        Assert.False(AnyProcessWithCommandLine(marker));
    }

    [Fact]
    public async Task Cancellation_kills_the_process()
    {
        Assert.SkipWhen(SkipReason() is not null, SkipReason() ?? string.Empty);
        using var cts = new CancellationTokenSource();
        var task = _runner.RunAsync(Req(string.Empty, _bound, "/bin/sleep", "60"), cts.Token);
        await Task.Delay(300, TestContext.Current.CancellationToken);
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => task);
    }

    [Fact]
    public async Task Command_exit_codes_are_enforced_by_the_host()
    {
        Assert.SkipWhen(SkipReason() is not null, SkipReason() ?? string.Empty);
        var ct = TestContext.Current.CancellationToken;
        var host = RealHost();

        await host.RunAsync(new PoolCommand(["/bin/sh", "-c", "exit 2"], [0, 2]), ct);
        var ex = await Assert.ThrowsAsync<PoolCommandFailedException>(() => host.RunAsync(new PoolCommand(["/bin/sh", "-c", "echo nope >&2; exit 2"]), ct));
        Assert.Contains("nope", ex.StdErr, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Firewall_is_applied_verified_and_gates_the_launch()
    {
        Assert.SkipWhen(SkipReason() is not null, SkipReason() ?? string.Empty);
        var ct = TestContext.Current.CancellationToken;
        var host = RealHost();
        var rule = new PoolFirewallRule(_first + 1, "127.0.0.1", 3128);
        var spec = new PoolLaunchSpec("s1", "agent-a", 1, rule.Uid, [], ["/bin/sleep", "60"], [], rule);

        // Nothing applied yet: refuse.
        await Assert.ThrowsAsync<PoolFirewallException>(() => host.CheckFirewallAsync(ct));
        await Assert.ThrowsAsync<PoolFirewallException>(() => host.StartAsync(spec, ct));

        await host.ApplyFirewallAsync([rule], ct);
        await host.CheckFirewallAsync(ct);

        var process = await host.StartAsync(spec, ct);
        var wait = host.WaitForExitAsync(process, ct);
        Assert.False(wait.IsCompleted);
        await host.StopAsync(process, ct);
        Assert.Equal(137, await wait.WaitAsync(_bound, ct));

        // Replacing the rule set replaces the accept lines; the old uid no longer launches.
        await host.ApplyFirewallAsync([], ct);
        await Assert.ThrowsAsync<PoolFirewallException>(() => host.StartAsync(spec, ct));

        // A flushed or deleted table is caught by the health check.
        await RunNftAsync("delete", "table", "inet", _table);
        await Assert.ThrowsAsync<PoolFirewallException>(() => host.CheckFirewallAsync(ct));
    }

    private static bool AnyProcessWithCommandLine(string marker)
    {
        foreach (var dir in Directory.EnumerateDirectories("/proc"))
        {
            try
            {
                if (File.ReadAllText(Path.Combine(dir, "cmdline")).Contains(marker, StringComparison.Ordinal))
                {
                    return true;
                }
            }
            catch (IOException)
            {
                // The process ended while it was being read.
            }
            catch (UnauthorizedAccessException)
            {
                // Not readable.
            }
        }

        return false;
    }

    private static async Task RunNftAsync(params string[] args)
    {
        var info = new ProcessStartInfo("/usr/sbin/nft") { UseShellExecute = false };
        foreach (var a in args)
        {
            info.ArgumentList.Add(a);
        }

        using var p = Process.Start(info) ?? throw new InvalidOperationException("nft did not start.");
        await p.WaitForExitAsync();
    }
}
