using System.Net;
using Bazaar.Server.BrowserPool.Abstractions;
using Bazaar.Server.BrowserPool.LinuxHost;
using Microsoft.Extensions.Configuration;

namespace Bazaar.Server.BrowserPool.LinuxHost.Tests;

/// <summary>
/// The active firewall probe (docs/design/59-desktop-density-and-guest-display.md, section 7) through the fake process runner: every
/// outcome of the blocked-target attempt and of the positive control, and what each one does to a launch.
/// </summary>
public sealed class FirewallProbeTests
{
    private const string _tool = "/opt/bazaar/bazaar-pool-spawn";
    private const string _vethHost = "10.77.0.5";

    private static readonly PoolFirewallRule _rule = new(10001, "10.0.0.2", 3128);

    private static string Listing(PoolFirewallRule rule) =>
        "table inet bazaar_pool {\n\tchain output {\n\t\ttype filter hook output priority filter; policy accept;\n"
        + $"\t\tmeta skuid {rule.Uid} ip daddr {rule.ProxyHost} tcp dport {rule.ProxyPort} accept\n"
        + "\t\tmeta skuid >= 10000 meta skuid <= 59999 drop\n\t}\n"
        + "\tchain input {\n\t\ttype filter hook input priority filter; policy accept;\n\t\tiifname \"bzv*\" drop\n\t}\n"
        + "\tchain forward {\n\t\ttype filter hook forward priority filter; policy accept;\n\t\tiifname \"bzv*\" drop\n\t}\n}\n";

    /// <summary>A per-slot launch as the pool renders it: namespace entry, privilege drop, sandbox, then the browser with the veth address as proxy.</summary>
    private static PoolLaunchSpec PerSlot(PoolFirewallRule? rule = null, string net = "--net=/run/netns/bzpool-1", string proxy = "--proxy-server=http://" + _vethHost + ":3128") =>
        new("s1", "agent-a", 1, 10001, [], ["/usr/bin/nsenter", net, "--", "/usr/bin/setpriv", "--reuid", "10001", "--", "/usr/bin/chromium", proxy], [], rule ?? _rule);

    /// <summary>A shared-namespace launch: no namespace entry, and the real proxy is the browser's.</summary>
    private static PoolLaunchSpec Shared() =>
        new("s1", "agent-a", 1, 10001, [], ["/usr/bin/setpriv", "--reuid", "10001", "--", "/usr/bin/chromium", "--proxy-server=http://10.0.0.2:3128"], [], _rule);

    private static LinuxPoolHostOptions Options(bool perSlot = true, TimeSpan? minInterval = null) =>
        new() { ProbeToolPath = _tool, SlotInterfacePrefix = perSlot ? "bzv" : null, ProbeMinInterval = minInterval ?? TimeSpan.Zero };

    /// <summary>The target of a probe request, as <c>host:port</c>, or null when the request is not a probe.</summary>
    private static string? Target(ProcessRequest r)
    {
        var i = r.Argv.ToList().IndexOf("--probe-connect");
        return i < 0 ? null : r.Argv[i + 1];
    }

    private sealed class FakeListener : IProbeListener
    {
        public int Port => 40123;

        public bool Connected { get; set; }

        public bool Disposed { get; private set; }

        public bool HasConnection() => Connected;

        public void Dispose() => Disposed = true;
    }

    private sealed class FakeFactory : IProbeListenerFactory
    {
        public FakeListener Listener { get; } = new();

        public List<(IPAddress Address, int Avoid)> Requests { get; } = [];

        public Exception? Failure { get; set; }

        public IProbeListener Listen(IPAddress address, int avoidPort)
        {
            Requests.Add((address, avoidPort));
            return Failure is null ? Listener : throw Failure;
        }
    }

    /// <summary>A runner that lists the table and answers each probe attempt with the given exit codes.</summary>
    private static FakeProcessRunner Runner(int blockedExit, int controlExit, FakeFactory? factory = null, Action<ProcessRequest>? onProbe = null) =>
        new(r =>
        {
            if (r.Argv.Count > 1 && r.Argv[1] == "list")
            {
                return FakeProcessRunner.Ok(Listing(_rule));
            }

            if (Target(r) is { } target)
            {
                onProbe?.Invoke(r);
                var isBlockedTarget = target.EndsWith(":" + (factory?.Listener.Port ?? 40123), StringComparison.Ordinal);
                return new ProcessResult(isBlockedTarget ? blockedExit : controlExit, string.Empty, string.Empty, false);
            }

            return FakeProcessRunner.Ok();
        });

    private static (LinuxPoolVmHost Host, FakeFactory Factory) Host(FakeProcessRunner runner, LinuxPoolHostOptions options)
    {
        var factory = new FakeFactory();
        return (new LinuxPoolVmHost(runner, _ => "table inet bazaar_pool\n", options, requireLinux: false, probeListeners: factory), factory);
    }

    private static List<ProcessRequest> Probes(FakeProcessRunner runner) => runner.Runs.Where(r => Target(r) is not null).ToList();

    [Theory]
    [InlineData(20)]
    [InlineData(21)]
    [InlineData(22)]
    [InlineData(23)]
    public async Task A_blocked_attempt_with_a_working_control_lets_the_launch_start(int blockedExit)
    {
        var runner = Runner(blockedExit, 0);
        var (host, factory) = Host(runner, Options());

        var process = await host.StartAsync(PerSlot(), TestContext.Current.CancellationToken);

        Assert.Equal("s1", process.SessionId);
        Assert.Single(runner.Started);
        Assert.Equal(2, Probes(runner).Count);
        Assert.True(factory.Listener.Disposed);
    }

    [Fact]
    public async Task A_connection_that_gets_through_fails_the_launch_and_nothing_starts()
    {
        var runner = Runner(0, 0);
        var (host, _) = Host(runner, Options());

        var ex = await Assert.ThrowsAsync<PoolFirewallException>(() => host.StartAsync(PerSlot(), TestContext.Current.CancellationToken));

        Assert.Contains("must be blocked", ex.Message, StringComparison.Ordinal);
        Assert.Empty(runner.Started);
    }

    [Fact]
    public async Task A_listener_that_saw_a_connection_fails_even_when_the_client_reports_a_block()
    {
        var runner = Runner(20, 0);
        var (host, factory) = Host(runner, Options());
        factory.Listener.Connected = true;

        var ex = await Assert.ThrowsAsync<PoolFirewallException>(() => host.StartAsync(PerSlot(), TestContext.Current.CancellationToken));

        Assert.Contains("listener", ex.Message, StringComparison.Ordinal);
        Assert.Empty(runner.Started);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(64)]
    [InlineData(70)]
    [InlineData(126)]
    [InlineData(127)]
    [InlineData(137)]
    public async Task An_exit_code_that_is_not_a_known_block_means_the_probe_did_not_run_and_fails(int blockedExit)
    {
        var runner = Runner(blockedExit, 0);
        var (host, _) = Host(runner, Options());

        var ex = await Assert.ThrowsAsync<PoolFirewallException>(() => host.StartAsync(PerSlot(), TestContext.Current.CancellationToken));

        Assert.Contains("could not run", ex.Message, StringComparison.Ordinal);
        Assert.Contains(blockedExit.ToString(System.Globalization.CultureInfo.InvariantCulture), ex.Message, StringComparison.Ordinal);
        Assert.Empty(runner.Started);
    }

    [Theory]
    [InlineData(20)]
    [InlineData(21)]
    [InlineData(1)]
    [InlineData(127)]
    public async Task A_failing_positive_control_fails_the_launch_even_when_the_blocked_attempt_looks_blocked(int controlExit)
    {
        var runner = Runner(20, controlExit);
        var (host, _) = Host(runner, Options());

        var ex = await Assert.ThrowsAsync<PoolFirewallException>(() => host.StartAsync(PerSlot(), TestContext.Current.CancellationToken));

        Assert.Contains("positive control", ex.Message, StringComparison.Ordinal);
        Assert.Empty(runner.Started);
    }

    [Theory]
    [MemberData(nameof(RunnerFailures))]
    public async Task A_probe_that_cannot_be_started_or_runs_out_of_time_fails_closed(Exception failure)
    {
        var runner = new FakeProcessRunner(r =>
            r.Argv.Count > 1 && r.Argv[1] == "list" ? FakeProcessRunner.Ok(Listing(_rule)) : throw failure);
        var (host, factory) = Host(runner, Options());

        var ex = await Assert.ThrowsAsync<PoolFirewallException>(() => host.StartAsync(PerSlot(), TestContext.Current.CancellationToken));

        Assert.Contains("could not run", ex.Message, StringComparison.Ordinal);
        Assert.Empty(runner.Started);
        Assert.True(factory.Listener.Disposed);
    }

    public static TheoryData<Exception> RunnerFailures => new()
    {
        new TimeoutException("slow"),
        new InvalidOperationException("Could not start the tool"),
        new System.ComponentModel.Win32Exception(2),
        new IOException("boom"),
    };

    [Fact]
    public async Task Cancellation_is_not_turned_into_a_probe_result()
    {
        var runner = new FakeProcessRunner(r =>
            r.Argv.Count > 1 && r.Argv[1] == "list" ? FakeProcessRunner.Ok(Listing(_rule)) : throw new OperationCanceledException());
        var (host, _) = Host(runner, Options());

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => host.StartAsync(PerSlot(), TestContext.Current.CancellationToken));
        Assert.Empty(runner.Started);
    }

    [Fact]
    public async Task A_listener_that_cannot_be_opened_fails_closed()
    {
        var runner = Runner(20, 0);
        var (host, factory) = Host(runner, Options());
        factory.Failure = new System.Net.Sockets.SocketException(98);

        await Assert.ThrowsAsync<PoolFirewallException>(() => host.StartAsync(PerSlot(), TestContext.Current.CancellationToken));
        Assert.Empty(Probes(runner));
        Assert.Empty(runner.Started);
    }

    [Fact]
    public async Task Per_slot_mode_probes_inside_the_slot_namespace_as_the_slot_uid_against_the_veth_address()
    {
        var runner = Runner(20, 0);
        var (host, factory) = Host(runner, Options());

        await host.StartAsync(PerSlot(), TestContext.Current.CancellationToken);

        var request = Assert.Single(Probes(runner), r => Target(r) == _vethHost + ":40123");
        var enter = new[] { "nsenter", "--net=/run/netns/bzpool-1", "--", "setpriv", "--reuid", "10001", "--regid", "10001", "--clear-groups", "--no-new-privs", "--inh-caps=-all", "--bounding-set=-all", "--", _tool, "--probe-connect", _vethHost + ":40123", "--timeout-ms", "300" };
        Assert.Equal(enter, request.Argv);
        Assert.Null(request.StdIn);

        // The control goes to the same veth address on the proxy's port, which the firewall DNATs; the listener is bound on the veth address.
        Assert.Single(Probes(runner), r => Target(r) == _vethHost + ":3128");
        Assert.Equal((IPAddress.Parse(_vethHost), 3128), factory.Requests.Single());
    }

    [Fact]
    public async Task Shared_mode_probes_loopback_in_the_host_namespace_and_the_control_goes_to_the_real_proxy()
    {
        var runner = Runner(20, 0);
        var (host, factory) = Host(runner, Options(perSlot: false));

        await host.StartAsync(Shared(), TestContext.Current.CancellationToken);

        var blocked = Assert.Single(Probes(runner), r => Target(r) == "127.0.0.1:40123");
        Assert.DoesNotContain("nsenter", blocked.Argv);
        Assert.Equal("setpriv", blocked.Argv[0]);
        Assert.Single(Probes(runner), r => Target(r) == "10.0.0.2:3128");
        Assert.Equal((IPAddress.Loopback, 3128), factory.Requests.Single());
    }

    [Fact]
    public async Task The_probe_runs_after_the_listing_check_and_before_the_launch()
    {
        var order = new List<string>();
        var runner = Runner(20, 0, onProbe: _ => order.Add("probe"));
        var (host, _) = Host(runner, Options());

        await host.StartAsync(PerSlot(), TestContext.Current.CancellationToken);

        Assert.Equal("nft", runner.Runs[0].Argv[0]);
        Assert.All(runner.Runs.Skip(1), r => Assert.NotNull(Target(r)));
        Assert.Equal(["probe", "probe"], order);
        Assert.Single(runner.Started);
    }

    [Fact]
    public async Task A_listing_failure_stops_the_launch_before_any_probe_runs()
    {
        var runner = new FakeProcessRunner(r => r.Argv.Count > 1 && r.Argv[1] == "list" ? FakeProcessRunner.Fail(1, "no such table") : FakeProcessRunner.Ok());
        var (host, _) = Host(runner, Options());

        await Assert.ThrowsAsync<PoolFirewallException>(() => host.StartAsync(PerSlot(), TestContext.Current.CancellationToken));
        Assert.Empty(Probes(runner));
    }

    [Fact]
    public async Task The_process_gets_the_connect_timeout_plus_a_grace_so_a_hung_tool_is_killed()
    {
        var runner = Runner(20, 0);
        var options = Options();
        options.ProbeTimeout = TimeSpan.FromMilliseconds(1500);
        var (host, _) = Host(runner, options);

        await host.StartAsync(PerSlot(), TestContext.Current.CancellationToken);

        Assert.All(Probes(runner), r =>
        {
            Assert.Equal("1500", r.Argv[^1]);
            Assert.Equal(TimeSpan.FromMilliseconds(1500) + TimeSpan.FromSeconds(5), r.Timeout);
        });
    }

    [Fact]
    public async Task A_disabled_probe_runs_nothing_and_does_not_need_a_tool()
    {
        var runner = Runner(0, 0);
        var options = Options();
        options.ProbeEnabled = false;
        options.ProbeToolPath = null;
        var (host, factory) = Host(runner, options);

        await host.StartAsync(Spec(), TestContext.Current.CancellationToken);

        Assert.Empty(Probes(runner));
        Assert.Empty(factory.Requests);
        Assert.Single(runner.Started);
    }

    [Fact]
    public async Task With_no_tool_configured_every_launch_is_refused()
    {
        var runner = Runner(20, 0);
        var options = Options();
        options.ProbeToolPath = null;
        var (host, _) = Host(runner, options);

        var ex = await Assert.ThrowsAsync<PoolFirewallException>(() => host.StartAsync(PerSlot(), TestContext.Current.CancellationToken));
        Assert.Contains("no tool", ex.Message, StringComparison.Ordinal);
        Assert.Empty(runner.Started);
    }

    [Fact]
    public async Task The_spawn_helper_is_the_probe_tool_when_no_probe_tool_is_set()
    {
        var runner = Runner(20, 0);
        var options = Options();
        options.ProbeToolPath = null;
        options.PoolSpawnHelperPath = "/opt/bazaar/helper";
        var host = new LinuxPoolVmHost(runner, _ => "table inet bazaar_pool\n", options, requireLinux: false, fifos: new FakeFifoFactory(), probeListeners: new FakeFactory());

        await host.StartAsync(PerSlot(), TestContext.Current.CancellationToken);

        Assert.All(Probes(runner), r => Assert.Contains("/opt/bazaar/helper", r.Argv));
    }

    [Fact]
    public async Task Within_the_minimum_interval_the_probe_is_skipped_and_without_one_it_runs_every_time()
    {
        var skipping = Runner(20, 0);
        var (hostA, _) = Host(skipping, Options(minInterval: TimeSpan.FromMinutes(5)));
        await hostA.StartAsync(PerSlot(), TestContext.Current.CancellationToken);
        await hostA.StartAsync(PerSlot(), TestContext.Current.CancellationToken);
        Assert.Equal(2, Probes(skipping).Count);

        var every = Runner(20, 0);
        var (hostB, _) = Host(every, Options());
        await hostB.StartAsync(PerSlot(), TestContext.Current.CancellationToken);
        await hostB.StartAsync(PerSlot(), TestContext.Current.CancellationToken);
        Assert.Equal(4, Probes(every).Count);
    }

    [Fact]
    public async Task A_failed_probe_is_not_remembered_as_a_pass()
    {
        var blockedExit = 0;
        var runner = new FakeProcessRunner(r =>
            r.Argv.Count > 1 && r.Argv[1] == "list" ? FakeProcessRunner.Ok(Listing(_rule))
            : Target(r) is { } t ? new ProcessResult(t.EndsWith(":40123", StringComparison.Ordinal) ? blockedExit : 0, string.Empty, string.Empty, false)
            : FakeProcessRunner.Ok());
        var (host, _) = Host(runner, Options(minInterval: TimeSpan.FromMinutes(5)));

        await Assert.ThrowsAsync<PoolFirewallException>(() => host.StartAsync(PerSlot(), TestContext.Current.CancellationToken));
        blockedExit = 20;
        await host.StartAsync(PerSlot(), TestContext.Current.CancellationToken);

        Assert.Equal(4, Probes(runner).Count);
    }

    public static TheoryData<string, bool> PlanCases => new()
    {
        { "per-slot ok", true },
        { "shared launch", true },
        { "namespace outside /run/netns", false },
        { "namespace with a traversal", false },
        { "two namespaces", false },
        { "proxy on another port", false },
        { "proxy is a name", false },
        { "no proxy", false },
        { "uid mismatch", false },
    };

    [Theory]
    [MemberData(nameof(PlanCases))]
    public void The_plan_accepts_only_what_the_pool_renders(string name, bool ok)
    {
        var spec = name switch
        {
            "per-slot ok" => PerSlot(),
            "shared launch" => Shared(),
            "namespace outside /run/netns" => PerSlot(net: "--net=/proc/1/ns/net"),
            "namespace with a traversal" => PerSlot(net: "--net=/run/netns/../x"),
            "two namespaces" => PerSlot() with { Launch = [.. PerSlot().Launch, "--net=/run/netns/other"] },
            "proxy on another port" => PerSlot(proxy: "--proxy-server=http://" + _vethHost + ":9999"),
            "proxy is a name" => PerSlot(proxy: "--proxy-server=http://proxy.example:3128"),
            "no proxy" => PerSlot() with { Launch = PerSlot().Launch.Where(e => !e.StartsWith("--proxy", StringComparison.Ordinal)).ToList() },
            "uid mismatch" => PerSlot() with { Uid = 10002 },
            _ => throw new InvalidOperationException(),
        };

        var planned = FirewallProbe.TryPlan(spec, out var plan, out var problem);

        Assert.Equal(ok, planned);
        Assert.Equal(ok, plan is not null);
        Assert.Equal(ok, problem is null);
    }

    [Fact]
    public void A_launch_with_a_namespace_is_per_slot_and_one_without_is_shared_whatever_the_options_say()
    {
        Assert.True(FirewallProbe.TryPlan(PerSlot(), out var perSlot, out _));
        Assert.Equal("/run/netns/bzpool-1", perSlot!.NetnsPath);
        Assert.Equal(_vethHost, perSlot.BlockedHost);
        Assert.Equal(_vethHost, perSlot.ControlHost);

        Assert.True(FirewallProbe.TryPlan(Shared(), out var shared, out _));
        Assert.Null(shared!.NetnsPath);
        Assert.Equal("127.0.0.1", shared.BlockedHost);
        Assert.Equal("10.0.0.2", shared.ControlHost);
    }

    [Fact]
    public async Task A_launch_that_cannot_be_planned_is_refused_before_anything_connects()
    {
        var runner = Runner(20, 0);
        var (host, factory) = Host(runner, Options());

        await Assert.ThrowsAsync<PoolFirewallException>(() => host.StartAsync(PerSlot(proxy: "--proxy-server=http://10.77.0.5:9999"), TestContext.Current.CancellationToken));
        Assert.Empty(Probes(runner));
        Assert.Empty(factory.Requests);
    }

    [Fact]
    public async Task Failure_messages_carry_no_argument_text_or_captured_output()
    {
        var runner = new FakeProcessRunner(r =>
            r.Argv.Count > 1 && r.Argv[1] == "list" ? FakeProcessRunner.Ok(Listing(_rule))
            : new ProcessResult(1, "secret-looking-stdout", "secret-looking-stderr", false));
        var (host, _) = Host(runner, Options());

        var ex = await Assert.ThrowsAsync<PoolFirewallException>(() => host.StartAsync(PerSlot(), TestContext.Current.CancellationToken));

        Assert.DoesNotContain("secret-looking", ex.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(_vethHost, ex.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("bzpool-1", ex.Message, StringComparison.Ordinal);
    }

    private static PoolLaunchSpec Spec() =>
        new("s1", "agent-a", 1, 10001, [], ["/usr/bin/chromium"], [], _rule);

    [Fact]
    public void Defaults_are_on_one_second_every_launch()
    {
        var o = new LinuxPoolHostOptions();
        Assert.True(o.ProbeEnabled);
        Assert.Null(o.ProbeToolPath);
        Assert.Equal(TimeSpan.FromMilliseconds(300), o.ProbeTimeout);
        Assert.Equal(TimeSpan.Zero, o.ProbeMinInterval);
        Assert.Equal("setpriv", o.SetprivPath);
        Assert.Equal("nsenter", o.NsenterPath);
        Assert.Empty(o.Validate());
    }

    [Fact]
    public void Options_bind_from_configuration()
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Bazaar:BrowserPool:LinuxHost:ProbeEnabled"] = "false",
            ["Bazaar:BrowserPool:LinuxHost:ProbeToolPath"] = "/usr/local/bin/bazaar-pool-spawn",
            ["Bazaar:BrowserPool:LinuxHost:ProbeTimeout"] = "00:00:02.500",
            ["Bazaar:BrowserPool:LinuxHost:ProbeMinInterval"] = "00:01:00",
            ["Bazaar:BrowserPool:LinuxHost:SetprivPath"] = "/usr/bin/setpriv",
            ["Bazaar:BrowserPool:LinuxHost:NsenterPath"] = "/usr/bin/nsenter",
        }).Build();

        var o = LinuxPoolHostOptions.Bind(config);

        Assert.False(o.ProbeEnabled);
        Assert.Equal("/usr/local/bin/bazaar-pool-spawn", o.ProbeToolPath);
        Assert.Equal(TimeSpan.FromMilliseconds(2500), o.ProbeTimeout);
        Assert.Equal(TimeSpan.FromMinutes(1), o.ProbeMinInterval);
        Assert.Equal("/usr/bin/setpriv", o.SetprivPath);
        Assert.Equal("/usr/bin/nsenter", o.NsenterPath);
    }

    [Fact]
    public void An_empty_probe_tool_path_means_null_and_a_bad_boolean_names_the_key_not_the_value()
    {
        var empty = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["Bazaar:BrowserPool:LinuxHost:ProbeToolPath"] = string.Empty }).Build();
        Assert.Null(LinuxPoolHostOptions.Bind(empty).ProbeToolPath);

        var bad = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["Bazaar:BrowserPool:LinuxHost:ProbeEnabled"] = "maybe-secret" }).Build();
        var ex = Assert.Throws<InvalidOperationException>(() => LinuxPoolHostOptions.Bind(bad));
        Assert.Contains("ProbeEnabled", ex.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("maybe-secret", ex.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("99")]
    [InlineData("00:00:00.050")]
    [InlineData("00:00:31")]
    [InlineData("00:00:00")]
    public void An_out_of_range_probe_timeout_is_refused(string value)
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["Bazaar:BrowserPool:LinuxHost:ProbeTimeout"] = value }).Build();
        Assert.Throws<InvalidOperationException>(() => LinuxPoolHostOptions.Bind(config));
    }

    [Fact]
    public void Probe_options_are_validated()
    {
        Assert.NotEmpty(new LinuxPoolHostOptions { ProbeMinInterval = TimeSpan.FromSeconds(-1) }.Validate());
        Assert.NotEmpty(new LinuxPoolHostOptions { ProbeMinInterval = TimeSpan.FromHours(2) }.Validate());
        Assert.NotEmpty(new LinuxPoolHostOptions { ProbeToolPath = "relative/tool" }.Validate());
        Assert.NotEmpty(new LinuxPoolHostOptions { ProbeToolPath = "/opt/../etc/x" }.Validate());
        Assert.NotEmpty(new LinuxPoolHostOptions { ProbeToolPath = "/opt/a b" }.Validate());
        Assert.NotEmpty(new LinuxPoolHostOptions { SetprivPath = "setpriv; rm" }.Validate());
        Assert.NotEmpty(new LinuxPoolHostOptions { NsenterPath = "/usr/../bin/nsenter" }.Validate());
        Assert.NotEmpty(new LinuxPoolHostOptions { NsenterPath = string.Empty }.Validate());
        Assert.Empty(new LinuxPoolHostOptions { ProbeToolPath = "/usr/local/bin/bazaar-pool-spawn", SetprivPath = "/usr/bin/setpriv" }.Validate());
    }
}
