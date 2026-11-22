using System.Net;
using System.Text.RegularExpressions;
using Bazaar.Server.BrowserPool.Abstractions;

namespace Bazaar.Server.BrowserPool;

/// <summary>
/// Builds the per-slot network namespace for <see cref="NetworkIsolation.PerSlotNamespace"/>: names, addresses, the setup and
/// cleanup argv lists, and the prefix that enters the namespace. It is pure. Every value that reaches an argv list is derived only
/// from the integer slot and the validated options (a fixed prefix plus an integer, an address computed from integers, or a
/// validated path), never from request text.
/// </summary>
/// <remarks>
/// Layout: slot N owns the /30 at <c>NetnsBaseAddress + 4N</c>. The pool VM holds the veth end <c>bzvN</c> at <c>+1</c>; the
/// namespace <c>bzpool-N</c> holds the peer <c>bzpN</c> at <c>+2</c> and has no route except that /30. The browser is told to use
/// the +1 address as its proxy; <see cref="NftablesRules"/> DNATs that to the real egress endpoint and drops everything else that
/// arrives from any <c>bzv*</c> interface.
/// </remarks>
public static partial class PoolNetwork
{
    /// <summary>The fixed prefix of every namespace name; the rest is the integer slot.</summary>
    public const string NamespacePrefix = "bzpool-";

    /// <summary>The fixed prefix of the VM-side veth names; the rest is the integer slot. The firewall matches <c>bzv*</c>.</summary>
    public const string HostInterfacePrefix = "bzv";

    /// <summary>The fixed prefix of the namespace-side veth names; the rest is the integer slot.</summary>
    public const string SlotInterfacePrefix = "bzp";

    /// <summary>Where iproute2 keeps the bind-mounted namespace files. Fixed by <c>ip netns</c>.</summary>
    public const string NamespaceDirectory = "/run/netns";

    /// <summary>Exit code 1 of the delete commands: "does not exist". Any other code is a real failure.</summary>
    private static readonly int[] _missingOk = [0, 1];

    /// <summary>The private blocks the range must sit in: (first address, size).</summary>
    private static readonly (uint First, uint Size)[] _blocks =
    [
        (0x0A000000u, 1u << 24), // 10.0.0.0/8
        (0xAC100000u, 1u << 20), // 172.16.0.0/12
        (0xC0A80000u, 1u << 16), // 192.168.0.0/16
        (0xA9FE0000u, 1u << 16), // 169.254.0.0/16
    ];

    /// <summary>The namespace name of a slot.</summary>
    public static string NamespaceName(int slot) => NamespacePrefix + PoolRequestValidator.Invariant(NonNegative(slot));

    /// <summary>The pool-VM end of the slot's veth pair.</summary>
    public static string HostInterface(int slot) => HostInterfacePrefix + PoolRequestValidator.Invariant(NonNegative(slot));

    /// <summary>The namespace end of the slot's veth pair.</summary>
    public static string SlotInterface(int slot) => SlotInterfacePrefix + PoolRequestValidator.Invariant(NonNegative(slot));

    /// <summary>Why the base address cannot hold <see cref="BrowserPoolOptions.MaxSessions"/> /30 blocks, or null when it can.</summary>
    public static string? CheckBase(BrowserPoolOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (options.MaxSessions is < 1 or > 1000)
        {
            return null; // reported by the MaxSessions check
        }

        if (!TryParse(options.NetnsBaseAddress, out var first))
        {
            return "NetnsBaseAddress must be an IPv4 address literal.";
        }

        if (first % 4 != 0)
        {
            return "NetnsBaseAddress must be a multiple of 4 (the first address of a /30).";
        }

        var last = (ulong)first + (4UL * (ulong)options.MaxSessions) - 1;
        foreach (var (start, size) in _blocks)
        {
            if (first >= start && last < (ulong)start + size)
            {
                return null;
            }
        }

        return "NetnsBaseAddress and the MaxSessions /30 blocks after it must fit inside 10.0.0.0/8, 172.16.0.0/12, 192.168.0.0/16 or 169.254.0.0/16.";
    }

    /// <summary>Why <see cref="BrowserPoolOptions.NetnsEgressInterface"/> is not acceptable, or null when it is empty or fine.</summary>
    public static string? CheckEgressInterface(BrowserPoolOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        var name = options.NetnsEgressInterface;
        if (name is null)
        {
            return "NetnsEgressInterface may not be null.";
        }

        if (name.Length == 0)
        {
            return null;
        }

        if (!InterfaceNamePattern().IsMatch(name))
        {
            return "NetnsEgressInterface must be 1 to 15 characters: a letter, then letters, digits and . _ - only.";
        }

        if (name is "all" or "default" or "lo"
            || name.StartsWith(HostInterfacePrefix, StringComparison.Ordinal)
            || name.StartsWith(SlotInterfacePrefix, StringComparison.Ordinal))
        {
            return "NetnsEgressInterface may not be all, default, lo or a pool veth.";
        }

        return null;
    }

    [GeneratedRegex(@"^[A-Za-z][A-Za-z0-9_.\-]{0,14}\z", RegexOptions.CultureInvariant)]
    private static partial Regex InterfaceNamePattern();

    /// <summary>The VM-side address of the slot's veth, which is what the browser uses as its proxy host.</summary>
    public static string HostAddress(BrowserPoolOptions options, int slot) => Format(Block(options, slot) + 1);

    /// <summary>The address of the namespace end of the slot's veth.</summary>
    public static string SlotAddress(BrowserPoolOptions options, int slot) => Format(Block(options, slot) + 2);

    /// <summary>True when the address lies inside any slot's /30, which an egress proxy may not do.</summary>
    public static bool IsInsideRange(BrowserPoolOptions options, string host)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (!TryParse(options.NetnsBaseAddress, out var first) || !TryParse(host, out var address))
        {
            return false;
        }

        return address >= first && (ulong)address < (ulong)first + (4UL * (ulong)options.MaxSessions);
    }

    /// <summary>True for any 127.x.x.x address, which needs <c>route_localnet</c> to be a DNAT target.</summary>
    public static bool IsLoopback(string host) => TryParse(host, out var address) && (address >> 24) == 127;

    /// <summary>
    /// Commands that give the slot its namespace and veth pair, in order. They start with an idempotent delete of anything left
    /// from an earlier run, so a stale name never makes the add fail and a failed delete still fails the add (closed).
    /// </summary>
    public static IReadOnlyList<PoolCommand> SetupCommands(BrowserPoolOptions options, int slot, PoolEgressEndpoint egress)
    {
        EnsureValid(options);
        EnsureSlot(options, slot);
        ArgumentNullException.ThrowIfNull(egress);
        var endpointError = PoolRequestValidator.ValidateEndpoint(egress);
        if (endpointError is not null)
        {
            throw new ArgumentException(endpointError, nameof(egress));
        }

        var ns = NamespaceName(slot);
        var host = HostInterface(slot);
        var peer = SlotInterface(slot);
        var ip = options.IpPath;
        var commands = new List<PoolCommand>();
        commands.AddRange(DeleteCommands(options, slot));
        commands.Add(new PoolCommand([ip, "netns", "add", ns]));
        commands.Add(new PoolCommand([ip, "link", "add", host, "type", "veth", "peer", "name", peer, "netns", ns]));
        commands.Add(new PoolCommand([ip, "addr", "add", HostAddress(options, slot) + "/30", "dev", host]));
        commands.Add(new PoolCommand([ip, "link", "set", host, "up"]));
        commands.Add(new PoolCommand([ip, "-n", ns, "addr", "add", SlotAddress(options, slot) + "/30", "dev", peer]));
        commands.Add(new PoolCommand([ip, "-n", ns, "link", "set", peer, "up"]));
        commands.Add(new PoolCommand([ip, "-n", ns, "link", "set", "lo", "up"]));

        // The DNAT to the real endpoint needs one kernel switch, scoped to this slot's veth and never "all" or "default".
        // A loopback target is martian for a packet that arrived on a veth unless route_localnet is on for that veth; any other
        // target is forwarded, which is off unless the veth it arrived on has forwarding on.
        var knob = IsLoopback(egress.Host) ? "route_localnet" : "forwarding";
        commands.Add(new PoolCommand([options.SysctlPath, "-w", "net.ipv4.conf." + host + "." + knob + "=1"]));

        // The reply from a forwarded target comes back in on the interface that reaches it, and must be forwarded to the veth too.
        if (!IsLoopback(egress.Host) && options.NetnsEgressInterface.Length != 0)
        {
            commands.Add(new PoolCommand([options.SysctlPath, "-w", "net.ipv4.conf." + options.NetnsEgressInterface + ".forwarding=1"]));
        }

        return commands;
    }

    /// <summary>
    /// Commands that remove the slot's veth and namespace, then check they are gone. Safe to run when nothing exists, and safe to
    /// run again. Only exit code 1 of the delete (which is "does not exist") is tolerated, and the check catches a delete that
    /// failed for another reason.
    /// </summary>
    public static IReadOnlyList<PoolCommand> CleanupCommands(BrowserPoolOptions options, int slot)
    {
        EnsureValid(options);
        EnsureSlot(options, slot);
        var commands = new List<PoolCommand>(DeleteCommands(options, slot))
        {
            new PoolCommand(["test", "!", "-e", NamespaceDirectory + "/" + NamespaceName(slot)]),
            new PoolCommand(["test", "!", "-e", "/sys/class/net/" + HostInterface(slot)]),
        };
        return commands;
    }

    /// <summary>
    /// The launch prefix that enters the slot's namespace. It goes BEFORE the privilege drop: entering a network namespace needs
    /// CAP_SYS_ADMIN, which the drop removes. <c>nsenter</c> is used rather than <c>ip netns exec</c> because it only calls
    /// <c>setns</c> and then <c>exec</c>; <c>ip netns exec</c> also unshares and remounts the mount namespace and <c>/sys</c>,
    /// which adds failure modes and privileged mount work that the sandbox does not need.
    /// </summary>
    public static IReadOnlyList<string> EnterPrefix(BrowserPoolOptions options, int slot)
    {
        EnsureValid(options);
        EnsureSlot(options, slot);
        return [options.NsenterPath, "--net=" + NamespaceDirectory + "/" + NamespaceName(slot), "--"];
    }

    private static IReadOnlyList<PoolCommand> DeleteCommands(BrowserPoolOptions options, int slot) =>
    [
        // Deleting the VM end removes the pair; deleting the namespace removes its end if the first delete did not run.
        new PoolCommand([options.IpPath, "link", "del", HostInterface(slot)], _missingOk),
        new PoolCommand([options.IpPath, "netns", "del", NamespaceName(slot)], _missingOk),
    ];

    private static uint Block(BrowserPoolOptions options, int slot)
    {
        EnsureValid(options);
        EnsureSlot(options, slot);
        // EnsureValid has already proved the base is a valid literal.
        return TryParse(options.NetnsBaseAddress, out var first)
            ? first + (4u * (uint)slot)
            : throw new ArgumentException("NetnsBaseAddress is not an IPv4 literal.", nameof(options));
    }

    private static int NonNegative(int slot) =>
        slot is >= 0 and <= 1000 ? slot : throw new ArgumentOutOfRangeException(nameof(slot), "The slot is outside the pool.");

    private static void EnsureValid(BrowserPoolOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        var errors = BrowserPoolOptionsValidator.Check(options);
        if (errors.Count != 0)
        {
            throw new ArgumentException("Invalid browser pool options: " + string.Join(' ', errors), nameof(options));
        }

        if (options.NetworkIsolation != NetworkIsolation.PerSlotNamespace)
        {
            throw new InvalidOperationException("Per-slot namespaces are not enabled.");
        }
    }

    private static void EnsureSlot(BrowserPoolOptions options, int slot)
    {
        if (slot < 0 || slot >= options.MaxSessions)
        {
            throw new ArgumentOutOfRangeException(nameof(slot), "The slot is outside the pool.");
        }
    }

    private static bool TryParse(string? text, out uint address)
    {
        address = 0;
        if (!PoolRequestValidator.IsIpv4Literal(text))
        {
            return false;
        }

        var bytes = IPAddress.Parse(text!).GetAddressBytes();
        address = ((uint)bytes[0] << 24) | ((uint)bytes[1] << 16) | ((uint)bytes[2] << 8) | bytes[3];
        return true;
    }

    private static string Format(uint address) =>
        PoolRequestValidator.Invariant((int)(address >> 24)) + "."
        + PoolRequestValidator.Invariant((int)((address >> 16) & 0xFF)) + "."
        + PoolRequestValidator.Invariant((int)((address >> 8) & 0xFF)) + "."
        + PoolRequestValidator.Invariant((int)(address & 0xFF));
}
