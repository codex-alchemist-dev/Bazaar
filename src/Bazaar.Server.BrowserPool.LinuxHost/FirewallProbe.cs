using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text.RegularExpressions;
using Bazaar.Server.BrowserPool.Abstractions;

namespace Bazaar.Server.BrowserPool.LinuxHost;

/// <summary>A TCP listener the host opens only to be connected to by a probe, and that it asks afterwards whether anyone got through.</summary>
internal interface IProbeListener : IDisposable
{
    /// <summary>The port it listens on.</summary>
    int Port { get; }

    /// <summary>True when a connection reached it, whether or not anything accepted it. The kernel completes the handshake by itself.</summary>
    bool HasConnection();
}

/// <summary>Opens <see cref="IProbeListener"/>s. A fake stands in for it in tests that cannot bind a slot's veth address.</summary>
internal interface IProbeListenerFactory
{
    /// <summary>Listens on an ephemeral port of <paramref name="address"/>, avoiding <paramref name="avoidPort"/>.</summary>
    IProbeListener Listen(IPAddress address, int avoidPort);
}

/// <summary>The real listener: a <see cref="TcpListener"/> that never accepts.</summary>
internal sealed class TcpProbeListenerFactory : IProbeListenerFactory
{
    public IProbeListener Listen(IPAddress address, int avoidPort)
    {
        // An ephemeral port can equal the proxy's port only by chance; the kernel picks another on the next try.
        for (var attempt = 0; attempt < 8; attempt++)
        {
            var listener = new TcpListener(address, 0);
            listener.Start(1);
            var port = ((IPEndPoint)listener.LocalEndpoint).Port;
            if (port != avoidPort)
            {
                return new Listener(listener, port);
            }

            listener.Stop();
        }

        throw new PoolFirewallException("The firewall probe could not get a listener port.");
    }

    private sealed class Listener : IProbeListener
    {
        private readonly TcpListener _listener;

        public Listener(TcpListener listener, int port)
        {
            _listener = listener;
            Port = port;
        }

        public int Port { get; }

        public bool HasConnection() => _listener.Pending();

        public void Dispose() => _listener.Stop();
    }
}

/// <summary>
/// The active firewall probe (docs/design/59-desktop-density-and-guest-display.md, section 7, "Browser pool isolation: what holds and
/// what does not"). The textual check (<see cref="NftListingCheck"/>) proves rules are listed; this proves packets are dropped, by
/// trying, as the slot's own uid and inside the slot's own network namespace, exactly as the launch will run.
/// <list type="bullet">
/// <item><b>Blocked target.</b> The host opens a listener it controls and that the slot must not reach: on the slot's VM-side veth
/// address (per-slot namespaces: the input chain's <c>iifname bzv* drop</c> must stop it) or on 127.0.0.1 (shared namespace: the output
/// chain's uid-range drop must stop it). The probe passes only if the attempt ends as a block (a timeout, a reset, unreachable or a
/// kernel refusal) and the listener saw no connection. A connection that got through is the failure.</item>
/// <item><b>Positive control.</b> The same uid, in the same namespace, connects to the slot's own proxy (the veth address on the proxy
/// port in per-slot mode, which the firewall DNATs to the real endpoint; the endpoint itself in shared mode). It must connect. This
/// keeps a probe that can never connect for an unrelated reason (no tool, wrong uid, no route) from counting as a pass. It needs the
/// egress proxy to be listening, which a launch needs anyway; no rule is widened for it.</item>
/// </list>
/// Everything is fail closed: a probe that cannot run, runs out of time in the process runner, exits with a code that is not a known
/// block, or throws, fails the check. The tool is <c>bazaar-pool-spawn --probe-connect</c>, a native AOT binary, so the image needs no
/// <c>nc</c> or <c>curl</c>; it is started from an argument vector with no shell. No page data or secret is involved, and a failure
/// message carries exit codes only.
/// </summary>
internal sealed partial class FirewallProbe
{
    // The exit codes of bazaar-pool-spawn --probe-connect (ProbeOutcome in Bazaar.Guest.PoolSpawn): 0 connected; these four blocked.
    private const int _exitTimedOut = 20;
    private const int _exitRefused = 21;
    private const int _exitUnreachable = 22;
    private const int _exitDenied = 23;

    // The most the process may run beyond the connect timeout: tool start, namespace entry and privilege drop.
    private static readonly TimeSpan _startupGrace = TimeSpan.FromSeconds(5);

    private readonly IProcessRunner _runner;
    private readonly IProbeListenerFactory _listeners;
    private readonly LinuxPoolHostOptions _options;
    private readonly Dictionary<int, long> _lastPassed = [];
    private readonly Lock _gate = new();

    public FirewallProbe(IProcessRunner runner, IProbeListenerFactory listeners, LinuxPoolHostOptions options)
    {
        _runner = runner;
        _listeners = listeners;
        _options = options;
    }

    /// <summary>The pure plan of one probe: where it runs and what it targets.</summary>
    /// <param name="NetnsPath">The slot's namespace file, or null in shared mode.</param>
    /// <param name="Uid">The slot uid.</param>
    /// <param name="BlockedHost">The address the host listens on and the slot must not reach.</param>
    /// <param name="ControlHost">The address of the slot's proxy as the slot sees it.</param>
    /// <param name="ControlPort">The proxy port.</param>
    internal sealed record Plan(string? NetnsPath, int Uid, string BlockedHost, string ControlHost, int ControlPort);

    /// <summary>
    /// Works out the probe from the launch the host is about to run. A launch that enters a namespace (<c>--net=</c> after the enter
    /// prefix) is per-slot: the probe needs that namespace file and the veth address (the browser's <c>--proxy-server</c>). A launch
    /// with no namespace is shared. A launch whose values are not what the pool renders gives a problem and no plan: the probe is
    /// then not possible and the launch must not happen.
    /// </summary>
    public static bool TryPlan(PoolLaunchSpec spec, out Plan? plan, out string? problem)
    {
        ArgumentNullException.ThrowIfNull(spec);
        plan = null;
        if (spec.Uid != spec.Firewall.Uid)
        {
            problem = "the launch's uid and its firewall rule's uid differ";
            return false;
        }

        // The probe follows the launch, not the configuration: it tests what this launch will do. No namespace entry means the shared
        // mode, where the output chain's uid rule is the only barrier and loopback is the blocked target.
        var nets = spec.Launch.Where(e => e.StartsWith("--net=", StringComparison.Ordinal)).ToList();
        if (nets.Count == 0)
        {
            plan = new Plan(null, spec.Uid, "127.0.0.1", spec.Firewall.ProxyHost, spec.Firewall.ProxyPort);
            problem = null;
            return true;
        }

        if (nets.Count != 1 || !NetnsPattern().IsMatch(nets[0]))
        {
            problem = "the launch does not enter exactly one slot network namespace";
            return false;
        }

        var proxies = spec.Launch.Where(e => e.StartsWith("--proxy-server=http://", StringComparison.Ordinal)).ToList();
        if (proxies.Count != 1 || !TryParseHostPort(proxies[0]["--proxy-server=http://".Length..], out var vethHost, out var vethPort) || vethPort != spec.Firewall.ProxyPort)
        {
            problem = "the launch does not name exactly one proxy address on the slot's port";
            return false;
        }

        plan = new Plan(nets[0]["--net=".Length..], spec.Uid, vethHost!, vethHost!, vethPort);
        problem = null;
        return true;
    }

    /// <summary>The argv of one probe connection: namespace entry (per-slot), the privilege drop the launch uses, then the tool.</summary>
    public static IReadOnlyList<string> Argv(LinuxPoolHostOptions options, string tool, Plan plan, string host, int port)
    {
        var uid = plan.Uid.ToString(CultureInfo.InvariantCulture);
        var argv = new List<string>();
        if (plan.NetnsPath is not null)
        {
            argv.AddRange([options.NsenterPath, "--net=" + plan.NetnsPath, "--"]);
        }

        argv.AddRange([options.SetprivPath, "--reuid", uid, "--regid", uid, "--clear-groups", "--no-new-privs", "--inh-caps=-all", "--bounding-set=-all", "--"]);
        argv.AddRange([tool, "--probe-connect", host + ":" + port.ToString(CultureInfo.InvariantCulture), "--timeout-ms", ((int)options.ProbeTimeout.TotalMilliseconds).ToString(CultureInfo.InvariantCulture)]);
        return argv;
    }

    /// <summary>Runs the probe for the launch unless it is off or one passed for the same uid within the minimum interval.</summary>
    /// <exception cref="PoolFirewallException">The probe failed, or could not run. The launch must not happen.</exception>
    public async Task RunAsync(PoolLaunchSpec spec, CancellationToken ct)
    {
        if (!_options.ProbeEnabled)
        {
            return;
        }

        if (RecentlyPassed(spec.Uid))
        {
            return;
        }

        var tool = _options.ProbeToolPath ?? _options.PoolSpawnHelperPath
            ?? throw new PoolFirewallException("The firewall probe has no tool: set ProbeToolPath or PoolSpawnHelperPath, or turn the probe off.");
        if (!TryPlan(spec, out var plan, out var problem) || plan is null)
        {
            throw new PoolFirewallException("The firewall probe cannot be planned: " + problem + ".");
        }

        IProbeListener listener;
        try
        {
            listener = _listeners.Listen(IPAddress.Parse(plan.BlockedHost), plan.ControlPort);
        }
        catch (Exception ex) when (ex is SocketException or InvalidOperationException or ArgumentException)
        {
            throw new PoolFirewallException("The firewall probe could not open its listener.", ex);
        }

        using (listener)
        {
            // Both connections at once: the blocked one waits out its timeout, so the added time is one timeout, not two.
            var blocked = RunOneAsync(Argv(_options, tool, plan, plan.BlockedHost, listener.Port), ct);
            var control = RunOneAsync(Argv(_options, tool, plan, plan.ControlHost, plan.ControlPort), ct);
            var exits = await Task.WhenAll(blocked, control).ConfigureAwait(false);
            var blockedExit = exits[0];
            var controlExit = exits[1];

            // The kernel finishes a handshake by itself, so a connection that got through is visible even if the client gave up.
            if (listener.HasConnection())
            {
                throw new PoolFirewallException("The firewall probe failed: a slot connection reached the host listener that must be blocked.");
            }

            if (blockedExit == 0)
            {
                throw new PoolFirewallException("The firewall probe failed: the slot connected to the address that must be blocked.");
            }

            if (!IsBlock(blockedExit))
            {
                throw new PoolFirewallException("The firewall probe could not run (blocked-target attempt exited " + blockedExit.ToString(CultureInfo.InvariantCulture) + ").");
            }

            if (controlExit != 0)
            {
                throw new PoolFirewallException("The firewall probe failed its positive control: the slot could not reach its own proxy (exit " + controlExit.ToString(CultureInfo.InvariantCulture) + "), so a block proves nothing.");
            }
        }

        lock (_gate)
        {
            _lastPassed[spec.Uid] = System.Diagnostics.Stopwatch.GetTimestamp();
        }
    }

    private static bool IsBlock(int exit) => exit is _exitTimedOut or _exitRefused or _exitUnreachable or _exitDenied;

    private static bool TryParseHostPort(string text, out string? host, out int port)
    {
        host = null;
        port = 0;
        var colon = text.LastIndexOf(':');
        if (colon <= 0 || !int.TryParse(text[(colon + 1)..], NumberStyles.None, CultureInfo.InvariantCulture, out port) || port is < 1 or > 65535)
        {
            return false;
        }

        var candidate = text[..colon];
        if (!IPAddress.TryParse(candidate, out var address) || address.AddressFamily != AddressFamily.InterNetwork || address.ToString() != candidate)
        {
            return false;
        }

        host = candidate;
        return true;
    }

    private bool RecentlyPassed(int uid)
    {
        if (_options.ProbeMinInterval <= TimeSpan.Zero)
        {
            return false;
        }

        lock (_gate)
        {
            return _lastPassed.TryGetValue(uid, out var at) && System.Diagnostics.Stopwatch.GetElapsedTime(at) < _options.ProbeMinInterval;
        }
    }

    /// <summary>Runs one probe process and returns its exit code; a failure to run it at all is a failed probe, never a pass.</summary>
    private async Task<int> RunOneAsync(IReadOnlyList<string> argv, CancellationToken ct)
    {
        try
        {
            var result = await _runner.RunAsync(new ProcessRequest(argv, null, _options.ProbeTimeout + _startupGrace, _options.MaxCapturedChars), ct).ConfigureAwait(false);
            return result.ExitCode;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            // A missing tool, a timeout of the whole process, a refused spawn: the probe did not run, so it did not pass.
            throw new PoolFirewallException("The firewall probe could not run: " + ex.GetType().Name + ".", ex);
        }
    }

    [GeneratedRegex(@"^--net=/run/netns/[A-Za-z0-9][A-Za-z0-9._\-]{0,63}\z", RegexOptions.CultureInvariant)]
    private static partial Regex NetnsPattern();
}
