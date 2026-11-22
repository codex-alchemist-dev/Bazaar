using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Runtime.Versioning;
using Bazaar.Server.BrowserPool.Abstractions;
using Bazaar.Server.BrowserPool.LinuxHost;

namespace Bazaar.Server.BrowserPool.LinuxHost.Tests;

/// <summary>
/// The active firewall probe against the real kernel (docs/design/59-desktop-density-and-guest-display.md, section 7): the pool
/// module's real rendered ruleset, real namespaces and veths set up by the pool's own commands, a real egress listener, and the
/// native <c>bazaar-pool-spawn --probe-connect</c> run under the pool's own <c>nsenter</c> and <c>setpriv</c>. Skipped, with the
/// reason, unless the real-tool gate is open (BAZAAR_TEST_REAL_NETNS=1, Linux, root) and BAZAAR_TEST_POOL_SPAWN_NATIVE names the
/// published native binary (dotnet publish src/Bazaar.Guest.PoolSpawn -c Release -r linux-x64): the slot's uid cannot run a
/// framework-dependent build, so the native one is the only probe tool that works here. The skip check runs before any options are
/// built. It uses its own table, slot 3 (namespace <c>bzpool-3</c>) and a private address range, and removes all of it again.
/// </summary>
[Collection("linuxhost-real")]
[SupportedOSPlatform("linux")]
public sealed class RealFirewallProbeTests : IDisposable
{
    private const string _table = "bazaar_pool_probetest";
    private const int _slot = 3;
    private const string _egressHost = "127.0.0.1";

    private static readonly TimeSpan _bound = TimeSpan.FromSeconds(60);

    private readonly string _unique = Guid.NewGuid().ToString("N")[..12];
    private readonly List<IDisposable> _disposables = [];
    private readonly ProcessRunner _runner = new();
    private BrowserPoolOptions? _pool;
    private string? _toolDir;
    private bool _touched;

    public void Dispose()
    {
        foreach (var d in _disposables)
        {
            d.Dispose();
        }

        if (_touched)
        {
            RunBlocking(["nft", "delete", "table", "inet", _table]);
            RunBlocking(["nft", "delete", "table", "inet", _table + "_decoy"]);
            if (_pool is not null)
            {
                foreach (var c in PoolCommandLine.CleanupCommands(_pool, _slot))
                {
                    RunBlocking(c.Argv);
                }
            }
        }

        if (_toolDir is not null)
        {
            Directory.Delete(_toolDir, true);
        }

        if (_pool is not null && Directory.Exists(_pool.SlotRoot))
        {
            Directory.Delete(Path.GetDirectoryName(_pool.SlotRoot)!, true);
        }
    }

    private static string? SkipReason(out string native)
    {
        native = string.Empty;
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

        foreach (var tool in new[] { "/usr/sbin/nft", "/usr/sbin/ip", "/usr/sbin/sysctl", "/usr/bin/nsenter", "/usr/bin/setpriv" })
        {
            if (!File.Exists(tool))
            {
                return tool + " is not installed.";
            }
        }

        var env = Environment.GetEnvironmentVariable("BAZAAR_TEST_POOL_SPAWN_NATIVE");
        if (string.IsNullOrEmpty(env) || !File.Exists(env))
        {
            return "Set BAZAAR_TEST_POOL_SPAWN_NATIVE to the published native bazaar-pool-spawn binary.";
        }

        native = env;

        // Never touch a live pool table or a leftover of an earlier run.
        return RunBlocking(["nft", "list", "table", "inet", _table]).Exit == 0 ? "The test table already exists." : null;
    }

    /// <summary>The native tool's path, or a skip with the reason. Called first in every test, before any options are built.</summary>
    private static string RequireReal()
    {
        var reason = SkipReason(out var native);
        Assert.SkipWhen(reason is not null, reason ?? string.Empty);
        return native;
    }

    private static (int Exit, string Out, string Err) RunBlocking(IReadOnlyList<string> argv)
    {
        var psi = new ProcessStartInfo(argv[0]) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        foreach (var a in argv.Skip(1))
        {
            psi.ArgumentList.Add(a);
        }

        using var p = Process.Start(psi)!;
        var err = p.StandardError.ReadToEndAsync();
        var output = p.StandardOutput.ReadToEnd();
        p.WaitForExit(60000);
        return (p.ExitCode, output, err.Result);
    }

    /// <summary>Starts a listener that accepts and drops connections, standing in for the agent's egress proxy.</summary>
    private int StartProxy()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var cts = new CancellationTokenSource();
        _disposables.Add(new Stop(listener, cts));
        _ = Task.Run(async () =>
        {
            try
            {
                while (true)
                {
                    (await listener.AcceptSocketAsync(cts.Token).ConfigureAwait(false)).Dispose();
                }
            }
            catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException or SocketException)
            {
            }
        });
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }

    private sealed class Stop(TcpListener listener, CancellationTokenSource cts) : IDisposable
    {
        public void Dispose()
        {
            cts.Cancel();
            listener.Stop();
            cts.Dispose();
        }
    }

    /// <summary>Builds the pool's real spec for the slot and runs its real prepare commands (directories, namespace, veth, sysctl).</summary>
    private PoolLaunchSpec Prepare(string native, bool perSlot, out LinuxPoolHostOptions hostOptions)
    {
        var root = "/tmp/bazaar-probe-test-" + _unique;
        _pool = new BrowserPoolOptions
        {
            SlotRoot = root + "/pool",
            UseSystemdRun = false,
            MemoryMaxMb = 0,
            MaxSessions = 8,
            MaxSessionsPerAgent = 2,
            NetnsBaseAddress = "10.231.96.0",
            NetworkIsolation = perSlot ? NetworkIsolation.PerSlotNamespace : NetworkIsolation.SharedWithUidRule,
        };
        Directory.CreateDirectory(root + "/pool");
        _touched = true;

        // The slot's uid must be able to run the tool, so it goes in a world-readable directory of its own.
        _toolDir = "/tmp/bazaar-probe-tool-" + _unique;
        Directory.CreateDirectory(_toolDir);
        File.SetUnixFileMode(_toolDir, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute | UnixFileMode.GroupRead | UnixFileMode.GroupExecute | UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
        var tool = _toolDir + "/bazaar-pool-spawn";
        File.Copy(native, tool);
        File.SetUnixFileMode(tool, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute | UnixFileMode.GroupRead | UnixFileMode.GroupExecute | UnixFileMode.OtherRead | UnixFileMode.OtherExecute);

        var proxyPort = StartProxy();
        var spec = PoolCommandLine.Build(_pool, "probe" + _unique, _slot, new PoolSessionRequest("agent-probe"), new PoolEgressEndpoint(_egressHost, proxyPort));
        foreach (var command in spec.Prepare)
        {
            var r = RunBlocking(command.Argv);
            Assert.True(command.AllowedExitCodes?.Contains(r.Exit) ?? r.Exit == 0, "prepare failed: " + command.Argv[0] + " " + r.Err);
        }

        hostOptions = new LinuxPoolHostOptions
        {
            TableName = _table,
            NftPath = "/usr/sbin/nft",
            FirewallTimeout = _bound,
            ProbeToolPath = tool,
            SetprivPath = "/usr/bin/setpriv",
            NsenterPath = "/usr/bin/nsenter",
            SlotInterfacePrefix = perSlot ? "bzv" : null,
        };
        return spec;
    }

    private string Render(IReadOnlyList<PoolFirewallRule> rules) =>
        NftablesRules.Render(rules, _pool!).Replace(NftablesRules.TableName, _table, StringComparison.Ordinal);

    private LinuxPoolVmHost Host(LinuxPoolHostOptions options) => new(Render, options);

    private FirewallProbe Probe(LinuxPoolHostOptions options) => new(_runner, new TcpProbeListenerFactory(), options);

    /// <summary>
    /// A table whose text passes <see cref="NftListingCheck"/>, accept and drop lines included, but whose rules sit in chains that are
    /// attached to no hook, so nothing is dropped. It is the case the textual check cannot see.
    /// </summary>
    private static string ListedButDeadRuleset(PoolFirewallRule rule, int first, int last, bool perSlot)
    {
        var text = $"table inet {_table}\ndelete table inet {_table}\ntable inet {_table} {{\n"
            + "  chain output {\n    type filter hook output priority 0; policy accept;\n  }\n"
            + "  chain output_rules {\n"
            + $"    meta skuid {rule.Uid} ip daddr {rule.ProxyHost} tcp dport {rule.ProxyPort} accept\n"
            + $"    meta skuid >= {first} meta skuid <= {last} drop\n  }}\n";
        if (perSlot)
        {
            text += "  chain input_rules {\n    iifname \"bzv*\" drop\n  }\n  chain forward_rules {\n    iifname \"bzv*\" drop\n  }\n";
        }

        return text + "}\n";
    }

    private static async Task<PoolFirewallException> Fails(FirewallProbe probe, PoolLaunchSpec spec) =>
        await Assert.ThrowsAsync<PoolFirewallException>(() => probe.RunAsync(spec, TestContext.Current.CancellationToken));

    [Fact]
    public async Task Per_slot_mode_with_the_real_table_the_probe_passes_and_the_launch_starts()
    {
        var native = RequireReal();
        var ct = TestContext.Current.CancellationToken;
        var spec = Prepare(native, perSlot: true, out var options);
        var host = Host(options);
        await host.ApplyFirewallAsync([spec.Firewall], ct);

        var sw = Stopwatch.StartNew();
        await Probe(options).RunAsync(spec, ct);
        sw.Stop();
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(5), "The probe adds about one probe timeout to a launch.");

        // Through the host: the same launch with the browser swapped for a sleeper (its namespace entry and proxy argument kept).
        var net = spec.Launch.Single(e => e.StartsWith("--net=", StringComparison.Ordinal));
        var proxy = spec.Launch.Single(e => e.StartsWith("--proxy-server=", StringComparison.Ordinal));
        var sleeper = spec with { Launch = ["/usr/bin/nsenter", net, "--", "/bin/sh", "-c", "exec sleep 30", "sh", proxy] };
        var process = await host.StartAsync(sleeper, ct);
        await host.StopAsync(process, ct);
    }

    [Fact]
    public async Task Per_slot_mode_with_the_table_deleted_the_probe_fails()
    {
        var native = RequireReal();
        var ct = TestContext.Current.CancellationToken;
        var spec = Prepare(native, perSlot: true, out var options);
        await Host(options).ApplyFirewallAsync([spec.Firewall], ct);
        Assert.Equal(0, RunBlocking(["nft", "delete", "table", "inet", _table]).Exit);

        var ex = await Fails(Probe(options), spec);

        Assert.Contains("must be blocked", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Per_slot_mode_with_the_veth_drop_rules_flushed_the_probe_fails()
    {
        var native = RequireReal();
        var ct = TestContext.Current.CancellationToken;
        var spec = Prepare(native, perSlot: true, out var options);
        await Host(options).ApplyFirewallAsync([spec.Firewall], ct);
        Assert.Equal(0, RunBlocking(["nft", "flush", "chain", "inet", _table, "input"]).Exit);
        Assert.Equal(0, RunBlocking(["nft", "flush", "chain", "inet", _table, "forward"]).Exit);

        var ex = await Fails(Probe(options), spec);

        Assert.Contains("must be blocked", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Per_slot_mode_a_table_that_lists_every_rule_but_drops_nothing_passes_the_text_check_and_fails_the_probe()
    {
        var native = RequireReal();
        var ct = TestContext.Current.CancellationToken;
        var spec = Prepare(native, perSlot: true, out var options);
        var dead = ListedButDeadRuleset(spec.Firewall, _pool!.SlotUidBase, _pool.MaxUid, perSlot: true);
        var file = "/tmp/bazaar-probe-dead-" + _unique + ".nft";
        File.WriteAllText(file, dead);
        try
        {
            var r = RunBlocking(["nft", "-f", file]);
            Assert.True(r.Exit == 0, r.Err);
        }
        finally
        {
            File.Delete(file);
        }

        // The textual check, which is all the host had before, is satisfied...
        var listing = RunBlocking(["nft", "list", "table", "inet", _table]).Out;
        Assert.Empty(NftListingCheck.Problems(listing, _table, [spec.Firewall], "bzv"));
        await Host(options).CheckFirewallAsync(ct);

        // ...and the probe is not fooled, nor is a launch.
        var ex = await Fails(Probe(options), spec);
        Assert.Contains("must be blocked", ex.Message, StringComparison.Ordinal);
        var launching = await Assert.ThrowsAsync<PoolFirewallException>(() => Host(options).StartAsync(spec, ct));
        Assert.Contains("probe", launching.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Per_slot_mode_a_missing_tool_fails_closed()
    {
        var native = RequireReal();
        var ct = TestContext.Current.CancellationToken;
        var spec = Prepare(native, perSlot: true, out var options);
        await Host(options).ApplyFirewallAsync([spec.Firewall], ct);
        options.ProbeToolPath = _toolDir + "/no-such-tool";

        var ex = await Fails(Probe(options), spec);

        Assert.Contains("could not run", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Per_slot_mode_with_no_proxy_listening_the_control_fails_the_probe()
    {
        var native = RequireReal();
        var ct = TestContext.Current.CancellationToken;
        var spec = Prepare(native, perSlot: true, out var options);
        foreach (var d in _disposables)
        {
            d.Dispose();
        }

        _disposables.Clear();
        await Host(options).ApplyFirewallAsync([spec.Firewall], ct);

        var ex = await Fails(Probe(options), spec);

        Assert.Contains("positive control", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Shared_mode_with_the_real_table_the_probe_passes()
    {
        var native = RequireReal();
        var ct = TestContext.Current.CancellationToken;
        var spec = Prepare(native, perSlot: false, out var options);
        await Host(options).ApplyFirewallAsync([spec.Firewall], ct);

        var sw = Stopwatch.StartNew();
        await Probe(options).RunAsync(spec, ct);
        sw.Stop();
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(5), "The probe adds about one probe timeout to a launch.");
    }

    [Fact]
    public async Task Shared_mode_with_the_table_deleted_or_listed_but_dead_the_probe_fails()
    {
        var native = RequireReal();
        var ct = TestContext.Current.CancellationToken;
        var spec = Prepare(native, perSlot: false, out var options);
        await Host(options).ApplyFirewallAsync([spec.Firewall], ct);
        Assert.Equal(0, RunBlocking(["nft", "delete", "table", "inet", _table]).Exit);
        var deleted = await Fails(Probe(options), spec);
        Assert.Contains("must be blocked", deleted.Message, StringComparison.Ordinal);

        var file = "/tmp/bazaar-probe-dead-" + _unique + ".nft";
        File.WriteAllText(file, ListedButDeadRuleset(spec.Firewall, _pool!.SlotUidBase, _pool.MaxUid, perSlot: false));
        try
        {
            Assert.Equal(0, RunBlocking(["nft", "-f", file]).Exit);
        }
        finally
        {
            File.Delete(file);
        }

        var listing = RunBlocking(["nft", "list", "table", "inet", _table]).Out;
        Assert.Empty(NftListingCheck.Problems(listing, _table, [spec.Firewall]));
        var dead = await Fails(Probe(options), spec);
        Assert.Contains("must be blocked", dead.Message, StringComparison.Ordinal);
    }
}
