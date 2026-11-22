using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text;
using Bazaar.Server.BrowserPool.Abstractions;

namespace Bazaar.Server.BrowserPool.Tests;

/// <summary>
/// Runs the REAL commands and rules of <see cref="NetworkIsolation.PerSlotNamespace"/> (real <c>ip</c>, <c>nft</c>, <c>nsenter</c>,
/// <c>setpriv</c>, bubblewrap and probe processes) and checks what a slot can and cannot reach. Skipped, with the precise reason, when
/// the environment lacks root, a tool, CAP_NET_ADMIN, nf_tables, veth or unprivileged user namespaces. It never fakes a pass.
/// It uses its own nft table (<see cref="Table"/>), slots 6 and 7 (namespaces <c>bzpool-6</c> and <c>bzpool-7</c>) and a private
/// address range, and removes all of it again. It does not touch any other table or namespace.
/// </summary>
[Collection("netns-real")]
public sealed class NetnsRealTests : IDisposable
{
    internal const string Table = "bazaar_pool_nstest";
    private const int _slotA = 6;
    private const int _slotB = 7;
    private const string _upstreamNs = "bzupt-nstest";
    private const string _upstreamHostIf = "bzupt0";
    private const string _upstreamPeerIf = "bzupt1";

    private readonly string _temp;
    private readonly BrowserPoolOptions _options;
    private readonly BrowserPoolOptions _shared;
    private readonly List<Process> _processes = [];
    private readonly List<IDisposable> _disposables = [];
    private bool _touchedNetwork;

    public NetnsRealTests()
    {
        _temp = Path.Combine("/tmp", "bazaar-netns-test-" + Guid.NewGuid().ToString("N")[..12]);
        _options = new BrowserPoolOptions
        {
            SlotRoot = _temp + "/pool",
            UseSystemdRun = false,
            MemoryMaxMb = 0,
            MaxSessions = 8,
            MaxSessionsPerAgent = 2,
            NetnsBaseAddress = "10.231.64.0",
            NetnsEgressInterface = _upstreamHostIf,
            NetworkIsolation = NetworkIsolation.PerSlotNamespace,
        };
        _shared = new BrowserPoolOptions
        {
            SlotRoot = _temp + "/pool",
            UseSystemdRun = false,
            MemoryMaxMb = 0,
            MaxSessions = 8,
            MaxSessionsPerAgent = 2,
            NetworkIsolation = NetworkIsolation.SharedWithUidRule,
        };
    }

    public void Dispose()
    {
        foreach (var p in _processes)
        {
            try
            {
                if (!p.HasExited)
                {
                    p.Kill(true);
                }
            }
            catch (InvalidOperationException)
            {
            }

            p.Dispose();
        }

        foreach (var d in _disposables)
        {
            d.Dispose();
        }

        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        if (_touchedNetwork)
        {
            RunBlocking(["nft", "delete", "table", "inet", Table]);
            foreach (var slot in new[] { _slotA, _slotB })
            {
                foreach (var c in PoolNetwork.CleanupCommands(_options, slot))
                {
                    RunBlocking(c.Argv);
                }
            }

            RunBlocking(["ip", "link", "del", _upstreamHostIf]);
            RunBlocking(["ip", "netns", "del", _upstreamNs]);
        }

        if (Directory.Exists(_temp))
        {
            foreach (var slot in new[] { _slotA, _slotB, 5 })
            {
                foreach (var c in PoolCommandLine.CleanupCommands(_shared, slot))
                {
                    RunBlocking(c.Argv);
                }
            }

            RunBlocking(["rm", "-rf", "--one-file-system", _temp]);
        }
    }

    // ---- environment ----

    private static readonly string[] _pythonCandidates = ["/usr/bin/python3", "/usr/local/bin/python3"];

    /// <summary>A python3 that works inside the sandbox: symlinks resolved (an alternatives link would point outside the bound /usr), and under /usr.</summary>
    internal static string? PythonPath()
    {
        foreach (var candidate in _pythonCandidates)
        {
            if (!File.Exists(candidate))
            {
                continue;
            }

            var resolved = new FileInfo(candidate).ResolveLinkTarget(returnFinalTarget: true)?.FullName ?? candidate;
            if (resolved.StartsWith("/usr/", StringComparison.Ordinal) && File.Exists(resolved))
            {
                return resolved;
            }
        }

        return null;
    }

    /// <summary>The environment variable that opts a machine in to the tests that touch its network setup.</summary>
    internal const string OptInVariable = "BAZAAR_TEST_REAL_NETNS";

    internal static string? SkipReason(BrowserPoolOptions options)
    {
        if (!OperatingSystem.IsLinux())
        {
            return "Not Linux.";
        }

        // These tests create network namespaces, veth pairs and nftables rules on the machine that runs them. That is fine in a
        // throwaway container and not fine on a shared build machine, so they run only when asked for.
        if (Environment.GetEnvironmentVariable(OptInVariable) != "1")
        {
            return "Set " + OptInVariable + "=1 to run the tests that change the machine's network setup.";
        }

        if (!File.ReadAllText("/proc/self/status").Contains("Uid:\t0\t", StringComparison.Ordinal))
        {
            return "Not running as root (the pool launcher is root in the pool VM).";
        }

        foreach (var tool in new[] { options.BwrapPath, options.SetprivPath, options.NsenterPath, options.IpPath, options.SysctlPath, "/usr/sbin/nft" })
        {
            if (!File.Exists(tool))
            {
                return tool + " is not installed.";
            }
        }

        if (PythonPath() is null)
        {
            return "python3 is not installed (the in-sandbox probes need it).";
        }

        // Capability probes with throw-away names, each removed again.
        var probe = "bzprobe" + Guid.NewGuid().ToString("N")[..6];
        var add = RunBlocking([options.IpPath, "netns", "add", probe]);
        if (add.Exit != 0)
        {
            return "Cannot create a network namespace (no CAP_SYS_ADMIN/CAP_NET_ADMIN or netns disabled): " + add.Err.Trim();
        }

        try
        {
            var veth = RunBlocking([options.IpPath, "link", "add", "bzpa" + probe[^4..], "type", "veth", "peer", "name", "bzpb" + probe[^4..], "netns", probe]);
            if (veth.Exit != 0)
            {
                return "Cannot create a veth pair (no veth module or CAP_NET_ADMIN): " + veth.Err.Trim();
            }

            RunBlocking([options.IpPath, "link", "del", "bzpa" + probe[^4..]]);
        }
        finally
        {
            RunBlocking([options.IpPath, "netns", "del", probe]);
        }

        var tableProbe = "bzprobe_" + Guid.NewGuid().ToString("N")[..6];
        var nft = RunBlocking(["nft", "add", "table", "inet", tableProbe]);
        if (nft.Exit != 0)
        {
            return "nftables is not usable (no nf_tables in the kernel or no CAP_NET_ADMIN): " + nft.Err.Trim();
        }

        RunBlocking(["nft", "delete", "table", "inet", tableProbe]);
        return null;
    }

    internal static (int Exit, string Out, string Err) RunBlocking(IReadOnlyList<string> argv, int timeoutMs = 30000)
    {
        var psi = new ProcessStartInfo(argv[0]) { RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var a in argv.Skip(1))
        {
            psi.ArgumentList.Add(a);
        }

        using var p = Process.Start(psi)!;
        var stdout = p.StandardOutput.ReadToEndAsync();
        var stderr = p.StandardError.ReadToEndAsync();
        if (!p.WaitForExit(timeoutMs))
        {
            p.Kill(true);
        }

        return (p.ExitCode, stdout.GetAwaiter().GetResult(), stderr.GetAwaiter().GetResult());
    }

    private static void Run(PoolCommand command)
    {
        var r = RunBlocking(command.Argv);
        var allowed = command.AllowedExitCodes ?? [0];
        Assert.True(allowed.Contains(r.Exit), "command failed (" + r.Exit + "): " + string.Join(' ', command.Argv) + " " + r.Err);
    }

    private void Directories(BrowserPoolOptions options, int slot)
    {
        Directory.CreateDirectory(_temp + "/pool");
        RunBlocking(["chmod", "0755", _temp]);
        RunBlocking(["chmod", "0711", _temp + "/pool"]);
        foreach (var c in PoolCommandLine.PrepareCommands(options, slot))
        {
            Run(c);
        }
    }

    /// <summary>Runs the real prepare list of the real spec for a slot: the directory and the whole network setup.</summary>
    private PoolLaunchSpec PrepareSlot(int slot, PoolEgressEndpoint egress)
    {
        _touchedNetwork = true;
        Directory.CreateDirectory(_temp + "/pool");
        RunBlocking(["chmod", "0755", _temp]);
        RunBlocking(["chmod", "0711", _temp + "/pool"]);
        var spec = PoolCommandLine.Build(_options, "nstest" + slot, slot, new PoolSessionRequest("agent-" + slot), egress);
        foreach (var c in spec.Prepare)
        {
            Run(c);
        }

        return spec;
    }

    /// <summary>Applies the real rendered document, with only the table name changed so the real pool's table is never touched.</summary>
    private void ApplyFirewall(IReadOnlyList<PoolFirewallRule> rules)
    {
        _touchedNetwork = true;
        var text = NftablesRules.Render(rules, _options).Replace(NftablesRules.TableName, Table, StringComparison.Ordinal);
        var file = _temp + "/rules.nft";
        File.WriteAllText(file, text);
        var r = RunBlocking(["nft", "-f", file]);
        Assert.True(r.Exit == 0, "nft -f failed: " + r.Err + "\n" + text);
    }

    private static string ListTable() => RunBlocking(["nft", "list", "table", "inet", Table]).Out;

    /// <summary>The probe runs inside the slot's namespace, then the same privilege drop and bubblewrap sandbox as the browser.</summary>
    private List<string> InSlot(int slot, params string[] program) =>
        [.. PoolNetwork.EnterPrefix(_options, slot), .. PoolCommandLine.SandboxPrefix(_options, slot, headed: false), .. program];

    private const string _connectScript =
        "import socket,sys\n"
        + "s=socket.socket(socket.AF_INET,socket.SOCK_STREAM)\n"
        + "s.settimeout(3)\n"
        + "try:\n"
        + "    s.connect((sys.argv[1],int(sys.argv[2])))\n"
        + "    print('OK',s.recv(16).decode())\n"
        + "except OSError as e:\n"
        + "    print('FAIL',e.errno)\n";

    private const string _abstractScript =
        "import socket,sys\n"
        + "s=socket.socket(socket.AF_UNIX,socket.SOCK_STREAM)\n"
        + "s.settimeout(3)\n"
        + "try:\n"
        + "    s.connect('\\0'+sys.argv[1])\n"
        + "    print('OK',s.recv(16).decode())\n"
        + "except OSError as e:\n"
        + "    print('FAIL',e.errno)\n";

    private string Connect(int slot, string host, int port) =>
        RunBlocking(InSlot(slot, PythonPath()!, "-I", "-c", _connectScript, host, port.ToString(CultureInfo.InvariantCulture))).Out.Trim();

    private static string Connect(IReadOnlyList<string> prefix, string host, int port) =>
        RunBlocking([.. prefix, "-I", "-c", _connectScript, host, port.ToString(CultureInfo.InvariantCulture)]).Out.Trim();

    private void RequireSandbox(int slot)
    {
        var probe = RunBlocking(InSlot(slot, "/usr/bin/id", "-u"));
        Assert.SkipWhen(probe.Exit != 0 && UserNamespaceDenied(probe.Err), "Unprivileged user namespaces are refused here: " + probe.Err.Trim());
        Assert.True(probe.Exit == 0, "the sandbox probe failed: " + probe.Err);
        Assert.Equal(PoolCommandLine.SlotUid(_options, slot).ToString(CultureInfo.InvariantCulture), probe.Out.Trim());
    }

    private static bool UserNamespaceDenied(string stderr) =>
        stderr.Contains("namespace", StringComparison.OrdinalIgnoreCase)
        || stderr.Contains("Operation not permitted", StringComparison.OrdinalIgnoreCase)
        || stderr.Contains("Permission denied", StringComparison.OrdinalIgnoreCase)
        || stderr.Contains("setting up uid map", StringComparison.OrdinalIgnoreCase);

    /// <summary>A TCP listener that answers every connection with a fixed word, counts them, and runs until disposed.</summary>
    private sealed class Listener : IDisposable
    {
        private readonly TcpListener _listener;
        private readonly string _word;
        private int _hits;

        public Listener(IPAddress address, string word)
        {
            _word = word;
            _listener = new TcpListener(address, 0);
            _listener.Start();
            Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
            _ = Task.Run(Loop);
        }

        public int Port { get; }

        public int Hits => Volatile.Read(ref _hits);

        public void Dispose() => _listener.Stop();

        private async Task Loop()
        {
            while (true)
            {
                try
                {
                    using var client = await _listener.AcceptTcpClientAsync();
                    Interlocked.Increment(ref _hits);
                    await client.GetStream().WriteAsync(Encoding.ASCII.GetBytes(_word));
                }
                catch (SocketException)
                {
                    return;
                }
                catch (ObjectDisposedException)
                {
                    return;
                }
                catch (IOException)
                {
                }
            }
        }
    }

    private Process StartProcess(List<string> argv)
    {
        var psi = new ProcessStartInfo(argv[0]) { RedirectStandardOutput = true };
        foreach (var a in argv.Skip(1))
        {
            psi.ArgumentList.Add(a);
        }

        var p = Process.Start(psi)!;
        _processes.Add(p);
        return p;
    }

    /// <summary>Reads one stdout line, failing instead of hanging when the process says nothing.</summary>
    private static string? ReadLine(Process process)
    {
        var read = process.StandardOutput.ReadLineAsync();
        Assert.True(read.Wait(TimeSpan.FromSeconds(20)), "The probe process printed nothing within 20 seconds.");
        return read.Result;
    }

    private static string? NonLoopbackAddress() =>
        System.Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces()
            .Where(n => n.OperationalStatus == System.Net.NetworkInformation.OperationalStatus.Up && n.NetworkInterfaceType != System.Net.NetworkInformation.NetworkInterfaceType.Loopback)
            .SelectMany(n => n.GetIPProperties().UnicastAddresses)
            .Select(a => a.Address)
            .FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetwork)?.ToString();

    // ---- the tests ----

    [Fact]
    public void Slot_reaches_the_proxy_and_nothing_else_in_the_host_namespace()
    {
        var reason = SkipReason(_options);
        Assert.SkipWhen(reason is not null, reason ?? string.Empty);
        var hostIp = NonLoopbackAddress();
        Assert.SkipWhen(hostIp is null, "The host has no non-loopback IPv4 address to test against.");

        using var proxy = new Listener(IPAddress.Loopback, "PROXY");
        using var other = new Listener(IPAddress.Any, "OTHER"); // reachable on every address of the host, loopback included
        var otherLoopback = new Listener(IPAddress.Loopback, "OTHER");
        _disposables.Add(otherLoopback);

        var spec = PrepareSlot(_slotA, new PoolEgressEndpoint("127.0.0.1", proxy.Port));
        RequireSandbox(_slotA);
        var hostVeth = PoolNetwork.HostAddress(_options, _slotA);

        // Control: with the namespace made but NO rules yet, the slot can reach the host's other listener through the veth.
        // This shows the isolation below comes from the rules, and that the probe can tell reachable from unreachable.
        Assert.Equal("OK OTHER", Connect(_slotA, hostVeth, other.Port));
        var hitsAfterControl = other.Hits;
        Assert.Equal(1, hitsAfterControl);

        ApplyFirewall([spec.Firewall]);

        // (a) the proxy stand-in, which listens on the VM's loopback, is reachable through the veth address.
        Assert.Equal("OK PROXY", Connect(_slotA, hostVeth, proxy.Port));
        Assert.Equal(1, proxy.Hits);

        // (b) everything else in the host namespace is not.
        Assert.StartsWith("FAIL", Connect(_slotA, hostVeth, other.Port), StringComparison.Ordinal); // another port on the veth address
        Assert.StartsWith("FAIL", Connect(_slotA, hostIp!, other.Port), StringComparison.Ordinal); // a non-loopback host address
        Assert.StartsWith("FAIL", Connect(_slotA, "127.0.0.1", proxy.Port), StringComparison.Ordinal); // the proxy's own address: the slot's lo is its own
        Assert.StartsWith("FAIL", Connect(_slotA, "127.0.0.1", otherLoopback.Port), StringComparison.Ordinal);
        Assert.StartsWith("FAIL", Connect(_slotA, hostVeth, proxy.Port + 1), StringComparison.Ordinal); // a port next to the proxy's
        Assert.Equal(hitsAfterControl, other.Hits);
        Assert.Equal(0, otherLoopback.Hits);
        Assert.Equal(1, proxy.Hits);

        // The nft document shows the same: one DNAT, accepted only for that flow, then a drop for every bzv* veth.
        var listing = ListTable();
        Assert.Contains("dnat ip to 127.0.0.1:" + proxy.Port.ToString(CultureInfo.InvariantCulture), listing, StringComparison.Ordinal);
        Assert.Contains("bzv*", listing, StringComparison.Ordinal);
    }

    [Fact]
    public void Slot_reaches_a_non_local_proxy_through_forwarding_and_masquerade()
    {
        var reason = SkipReason(_options);
        Assert.SkipWhen(reason is not null, reason ?? string.Empty);

        // A second namespace plays "another host" that runs the proxy: the DNAT target is then forwarded, not delivered locally.
        _touchedNetwork = true;
        Run(new PoolCommand([_options.IpPath, "netns", "add", _upstreamNs]));
        Run(new PoolCommand([_options.IpPath, "link", "add", _upstreamHostIf, "type", "veth", "peer", "name", _upstreamPeerIf, "netns", _upstreamNs]));
        Run(new PoolCommand([_options.IpPath, "addr", "add", "10.232.31.1/30", "dev", _upstreamHostIf]));
        Run(new PoolCommand([_options.IpPath, "link", "set", _upstreamHostIf, "up"]));
        Run(new PoolCommand([_options.IpPath, "-n", _upstreamNs, "addr", "add", "10.232.31.2/30", "dev", _upstreamPeerIf]));
        Run(new PoolCommand([_options.IpPath, "-n", _upstreamNs, "link", "set", _upstreamPeerIf, "up"]));

        const string serverScript =
            "import socket,sys\n"
            + "s=socket.socket();s.setsockopt(socket.SOL_SOCKET,socket.SO_REUSEADDR,1)\n"
            + "s.bind(('10.232.31.2',3128));s.listen();print('READY',flush=True)\n"
            + "s.settimeout(40)\n"
            + "while True:\n"
            + "    c,a=s.accept();print('PEER',a[0],flush=True);c.send(b'UPSTREAM');c.close()\n";
        var server = StartProcess([_options.NsenterPath, "--net=/run/netns/" + _upstreamNs, "--", PythonPath()!, "-I", "-u", "-c", serverScript]);
        Assert.Equal("READY", ReadLine(server));

        var spec = PrepareSlot(_slotA, new PoolEgressEndpoint("10.232.31.2", 3128));
        RequireSandbox(_slotA);
        ApplyFirewall([spec.Firewall]);
        var hostVeth = PoolNetwork.HostAddress(_options, _slotA);

        Assert.Equal("OK UPSTREAM", Connect(_slotA, hostVeth, 3128));
        // The upstream saw the VM's address on the upstream link, not the slot's: the masquerade works.
        Assert.Equal("PEER 10.232.31.1", ReadLine(server));

        // The upstream's other address and ports stay unreachable from the slot.
        Assert.StartsWith("FAIL", Connect(_slotA, "10.232.31.2", 3128), StringComparison.Ordinal);
        Assert.StartsWith("FAIL", Connect(_slotA, hostVeth, 3129), StringComparison.Ordinal);
        Assert.Contains("net.ipv4.conf.bzv6.forwarding", string.Join(' ', spec.Prepare.SelectMany(c => c.Argv)), StringComparison.Ordinal);
        Assert.Equal("0", File.ReadAllText("/proc/sys/net/ipv4/conf/default/forwarding").Trim());
    }

    [Fact]
    public void Abstract_unix_socket_in_the_host_namespace_is_unreachable_from_a_slot_but_reachable_in_shared_mode()
    {
        var reason = SkipReason(_options);
        Assert.SkipWhen(reason is not null, reason ?? string.Empty);

        var name = "bazaar-nstest-" + Guid.NewGuid().ToString("N");
        using var server = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        server.Bind(new UnixDomainSocketEndPoint("\0" + name));
        server.Listen(8);
        var hits = 0;
        _ = Task.Run(() =>
        {
            while (true)
            {
                try
                {
                    using var c = server.Accept();
                    Interlocked.Increment(ref hits);
                    c.Send(Encoding.ASCII.GetBytes("ABSTRACT"));
                }
                catch (SocketException)
                {
                    return;
                }
                catch (ObjectDisposedException)
                {
                    return;
                }
            }
        }, TestContext.Current.CancellationToken);

        using var proxy = new Listener(IPAddress.Loopback, "PROXY");
        PrepareSlot(_slotA, new PoolEgressEndpoint("127.0.0.1", proxy.Port));
        Directories(_shared, 5);
        RequireSandbox(_slotA);

        // Shared mode (today's behaviour): same uid drop and bubblewrap, no namespace. The socket IS reachable.
        var sharedPrefix = PoolCommandLine.SandboxPrefix(_shared, 5, headed: false);
        var sharedProbe = RunBlocking([.. sharedPrefix, "/usr/bin/id", "-u"]);
        Assert.SkipWhen(sharedProbe.Exit != 0 && UserNamespaceDenied(sharedProbe.Err), "Unprivileged user namespaces are refused here: " + sharedProbe.Err.Trim());
        Assert.Equal("OK ABSTRACT", RunBlocking([.. sharedPrefix, PythonPath()!, "-I", "-c", _abstractScript, name]).Out.Trim());
        Assert.Equal(1, Volatile.Read(ref hits));

        // Per-slot mode: the same probe from inside the slot's namespace cannot connect, and the server never saw it.
        var perSlot = RunBlocking(InSlot(_slotA, PythonPath()!, "-I", "-c", _abstractScript, name)).Out.Trim();
        Assert.StartsWith("FAIL", perSlot, StringComparison.Ordinal);
        Assert.Equal(1, Volatile.Read(ref hits));
    }

    [Fact]
    public void Two_slots_cannot_reach_each_other()
    {
        var reason = SkipReason(_options);
        Assert.SkipWhen(reason is not null, reason ?? string.Empty);

        using var proxyA = new Listener(IPAddress.Loopback, "PROXYA");
        using var proxyB = new Listener(IPAddress.Loopback, "PROXYB");
        var specA = PrepareSlot(_slotA, new PoolEgressEndpoint("127.0.0.1", proxyA.Port));
        var specB = PrepareSlot(_slotB, new PoolEgressEndpoint("127.0.0.1", proxyB.Port));
        RequireSandbox(_slotA);
        RequireSandbox(_slotB);
        ApplyFirewall([specA.Firewall, specB.Firewall]);

        // A server inside slot B's namespace, as slot B's user, in the same sandbox: it listens on every address of its namespace.
        const string serverScript =
            "import socket\n"
            + "s=socket.socket();s.setsockopt(socket.SOL_SOCKET,socket.SO_REUSEADDR,1)\n"
            + "s.bind(('0.0.0.0',9099));s.listen();print('READY',flush=True)\n"
            + "while True:\n"
            + "    c,a=s.accept();c.send(b'SLOTB');c.close()\n";
        var serverB = StartProcess(InSlot(_slotB, PythonPath()!, "-I", "-u", "-c", serverScript));
        Assert.Equal("READY", ReadLine(serverB));

        var slotBAddress = PoolNetwork.SlotAddress(_options, _slotB);
        var hostVethB = PoolNetwork.HostAddress(_options, _slotB);

        // Controls: the server is alive. Slot B reaches it on its own loopback and on its own address, so a failure from
        // slot A below is the network boundary and not a dead server.
        Assert.Equal("OK SLOTB", Connect(_slotB, "127.0.0.1", 9099));
        Assert.Equal("OK SLOTB", Connect(_slotB, slotBAddress, 9099));

        // Slot A cannot reach slot B's addresses, its veth address, or its proxy port; both slots still reach their own proxy.
        Assert.StartsWith("FAIL", Connect(_slotA, slotBAddress, 9099), StringComparison.Ordinal);
        Assert.StartsWith("FAIL", Connect(_slotA, hostVethB, 9099), StringComparison.Ordinal);
        Assert.StartsWith("FAIL", Connect(_slotA, hostVethB, proxyB.Port), StringComparison.Ordinal);
        Assert.StartsWith("FAIL", Connect(_slotB, PoolNetwork.SlotAddress(_options, _slotA), 9099), StringComparison.Ordinal);
        Assert.Equal("OK PROXYA", Connect(_slotA, PoolNetwork.HostAddress(_options, _slotA), proxyA.Port));
        Assert.Equal("OK PROXYB", Connect(_slotB, hostVethB, proxyB.Port));
        Assert.Equal(1, proxyB.Hits);
        Assert.Equal(1, proxyA.Hits);
    }

    [Fact]
    public void Cleanup_removes_the_namespace_the_veth_and_the_rules_and_is_idempotent()
    {
        var reason = SkipReason(_options);
        Assert.SkipWhen(reason is not null, reason ?? string.Empty);

        using var proxy = new Listener(IPAddress.Loopback, "PROXY");
        var spec = PrepareSlot(_slotA, new PoolEgressEndpoint("127.0.0.1", proxy.Port));
        ApplyFirewall([spec.Firewall]);
        Assert.True(File.Exists("/run/netns/bzpool-6"));
        Assert.Equal(0, RunBlocking(["ip", "link", "show", "dev", "bzv6"]).Exit);
        Assert.Contains("bzv6", ListTable(), StringComparison.Ordinal);

        // What the service runs at close, then what it does next: re-apply the rules for the sessions that remain (none).
        foreach (var c in spec.Cleanup)
        {
            Run(c);
        }

        ApplyFirewall([]);

        Assert.False(File.Exists("/run/netns/bzpool-6"));
        Assert.False(Directory.Exists("/sys/class/net/bzv6"));
        Assert.NotEqual(0, RunBlocking(["ip", "link", "show", "dev", "bzv6"]).Exit);
        Assert.DoesNotContain("bzpool-6", RunBlocking(["ip", "netns", "list"]).Out, StringComparison.Ordinal);
        Assert.DoesNotContain("\"bzv6\"", ListTable(), StringComparison.Ordinal);
        Assert.DoesNotContain("dnat", ListTable(), StringComparison.Ordinal);
        Assert.False(Directory.Exists(PoolCommandLine.SlotDirectory(_options, _slotA)));

        // Running the same cleanup again, with nothing left, still succeeds.
        foreach (var c in spec.Cleanup)
        {
            Run(c);
        }
    }

    [Fact]
    public void Cleanup_removes_a_half_made_namespace_and_a_stale_one_does_not_block_a_new_open()
    {
        var reason = SkipReason(_options);
        Assert.SkipWhen(reason is not null, reason ?? string.Empty);

        _touchedNetwork = true;
        var setup = PoolNetwork.SetupCommands(_options, _slotA, new PoolEgressEndpoint("127.0.0.1", 3128));
        // An open that failed after the veth was made but before the addresses.
        foreach (var c in setup.Take(4))
        {
            Run(c);
        }

        Assert.True(File.Exists("/run/netns/bzpool-6"));
        Assert.Equal(0, RunBlocking(["ip", "link", "show", "dev", "bzv6"]).Exit);

        foreach (var c in PoolCommandLine.CleanupCommands(_options, _slotA))
        {
            Run(c);
        }

        Assert.False(File.Exists("/run/netns/bzpool-6"));
        Assert.NotEqual(0, RunBlocking(["ip", "link", "show", "dev", "bzv6"]).Exit);

        // A namespace left behind by a crash is removed by the next setup's own delete step, so the add does not fail.
        foreach (var c in setup.Take(4))
        {
            Run(c);
        }

        foreach (var c in setup)
        {
            Run(c);
        }

        Assert.True(File.Exists("/run/netns/bzpool-6"));
    }
}
