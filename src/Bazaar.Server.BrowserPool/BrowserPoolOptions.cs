using System.Text.RegularExpressions;
using Microsoft.Extensions.Options;

namespace Bazaar.Server.BrowserPool;

/// <summary>How a slot's browser is cut off from the pool VM's network. The choice is the operator's; the default is the stronger one.</summary>
public enum NetworkIsolation
{
    /// <summary>
    /// Every slot runs in its own network namespace joined to the pool VM by a veth pair, and the VM drops everything from that
    /// veth except TCP to the agent's egress proxy (reached through a DNAT). Needs CAP_NET_ADMIN, <c>ip</c>, <c>nsenter</c> and <c>sysctl</c> in the pool VM.
    /// </summary>
    PerSlotNamespace = 0,

    /// <summary>
    /// Every slot shares the pool VM's network namespace and a per-uid nftables rule limits it to its proxy. Needs fewer host
    /// privileges, but abstract UNIX sockets, netlink state and loopback are shared with every other process in the VM.
    /// </summary>
    SharedWithUidRule = 1,
}

/// <summary>
/// Operator settings for the browser pool (config section <c>Bazaar:BrowserPool</c>). Every path and number ends up in an argv
/// list that runs as root in the pool VM, so <see cref="BrowserPoolOptionsValidator"/> accepts only a narrow character set and
/// the command builder re-checks it, so a value that skipped validation still cannot inject a flag.
/// </summary>
public sealed class BrowserPoolOptions
{
    /// <summary>The configuration section name.</summary>
    public const string SectionName = "Bazaar:BrowserPool";

    /// <summary>Off refuses every open with <c>Disabled</c>.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>Maximum sessions in the pool VM, quarantined slots included.</summary>
    public int MaxSessions { get; set; } = 32;

    /// <summary>Maximum sessions per agent, opening ones included.</summary>
    public int MaxSessionsPerAgent { get; set; } = 4;

    /// <summary>Slot N runs as uid and gid <c>SlotUidBase + N</c>. At least 10000; <c>SlotUidBase + MaxSessions</c> at most 59999.</summary>
    public int SlotUidBase { get; set; } = 20000;

    /// <summary>Directory in the pool VM that holds <c>slot-N</c> directories. Absolute, at least two segments, root-owned and not writable by the slot users (the host provisions it).</summary>
    public string SlotRoot { get; set; } = "/var/lib/bazaar-pool";

    /// <summary>Chromium binary in the pool VM.</summary>
    public string ChromiumPath { get; set; } = "/usr/bin/chromium";

    /// <summary>Bubblewrap binary in the pool VM.</summary>
    public string BwrapPath { get; set; } = "/usr/bin/bwrap";

    /// <summary>The <c>setpriv</c> binary (util-linux) in the pool VM.</summary>
    public string SetprivPath { get; set; } = "/usr/bin/setpriv";

    /// <summary>The <c>mount</c> binary (util-linux), used to put a size-limited tmpfs on a slot directory when <see cref="DiskMaxMb"/> is above 0.</summary>
    public string MountPath { get; set; } = "/usr/bin/mount";

    /// <summary>The <c>umount</c> binary (util-linux) that removes that tmpfs at cleanup.</summary>
    public string UmountPath { get; set; } = "/usr/bin/umount";

    /// <summary>The <c>cage</c> kiosk compositor binary, used for headed sessions.</summary>
    public string CagePath { get; set; } = "/usr/bin/cage";

    /// <summary>Per-slot network namespace (default) or the shared namespace with a per-uid rule.</summary>
    public NetworkIsolation NetworkIsolation { get; set; } = NetworkIsolation.PerSlotNamespace;

    /// <summary>
    /// First address of the range that holds one /30 per slot (slot N gets <c>base + 4N</c>: host end +1, slot end +2). Used with
    /// <see cref="NetworkIsolation.PerSlotNamespace"/>. An IPv4 literal inside 10.0.0.0/8, 172.16.0.0/12, 192.168.0.0/16 or
    /// 169.254.0.0/16, a multiple of 4, with room for <see cref="MaxSessions"/> blocks inside the same private block. An egress proxy
    /// may never sit inside this range.
    /// </summary>
    public string NetnsBaseAddress { get; set; } = "10.222.0.0";

    /// <summary>
    /// The pool VM's network interface that reaches the egress proxy, when the proxy is not an address of the VM itself (a proxy on
    /// the VM's loopback or on one of its own addresses needs none). Linux only forwards a packet that arrives on an interface with
    /// forwarding on, and the proxy's reply arrives on this one, so the pool turns forwarding on for this interface only, never
    /// <c>all</c> or <c>default</c>. Empty means the proxy is local. A letter, then letters, digits and <c>. _ -</c>, at most 15 characters.
    /// </summary>
    public string NetnsEgressInterface { get; set; } = string.Empty;

    /// <summary>The <c>ip</c> (iproute2) binary in the pool VM, used for namespaces, veth pairs and addresses.</summary>
    public string IpPath { get; set; } = "/usr/sbin/ip";

    /// <summary>The <c>nsenter</c> binary (util-linux) that puts the launch into the slot's namespace.</summary>
    public string NsenterPath { get; set; } = "/usr/bin/nsenter";

    /// <summary>The <c>sysctl</c> binary (procps), used to scope <c>route_localnet</c> or <c>forwarding</c> to one slot's veth.</summary>
    public string SysctlPath { get; set; } = "/usr/sbin/sysctl";

    /// <summary>Hard lifetime of a session.</summary>
    public TimeSpan SessionLifetime { get; set; } = TimeSpan.FromHours(2);

    /// <summary>A session with no <c>Touch</c> for this long is closed by the sweep.</summary>
    public TimeSpan IdleTimeout { get; set; } = TimeSpan.FromMinutes(20);

    /// <summary>An open that has not finished after this long is abandoned and cleaned up.</summary>
    public TimeSpan OpenTimeout { get; set; } = TimeSpan.FromMinutes(2);

    /// <summary>Memory ceiling per session in MB through a cgroup scope; 0 means no cgroup limit.</summary>
    public int MemoryMaxMb { get; set; } = 1024;

    /// <summary>
    /// Processor ceiling per session through the same cgroup scope, as a percentage of one core: 200 means two cores' worth of time.
    /// 0 means no limit. One busy page can then not starve the other sessions on the pool VM.
    /// </summary>
    public int CpuQuotaPercent { get; set; } = 200;

    /// <summary>The most processes and threads one session may have, through the same cgroup scope. Stops a fork bomb inside a slot.</summary>
    public int TasksMax { get; set; } = 512;

    /// <summary>
    /// Disk ceiling per slot in MB: the slot directory (profile and runtime directory) is a tmpfs of this size, so one session cannot
    /// fill the pool VM's disk. 0 means no cap (the directory is plain disk, as before); otherwise 16 to 65536. tmpfs pages are
    /// charged to the memory cgroup of the process that writes them, so while <see cref="MemoryMaxMb"/> is on this must be smaller than it.
    /// </summary>
    public int DiskMaxMb { get; set; } = 256;

    /// <summary>The most files and directories the slot tmpfs may hold (its <c>nr_inodes</c>). Used only when <see cref="DiskMaxMb"/> is above 0; 1024 to 16777216.</summary>
    public int DiskMaxInodes { get; set; } = 65_536;

    /// <summary>Wrap the launch in <c>systemd-run --scope</c> so the memory, processor and task limits apply.</summary>
    public bool UseSystemdRun { get; set; } = true;

    /// <summary>
    /// Extra read-only directories to bind into the sandbox at the same path, for a Chromium that is not under <c>/usr</c>.
    /// Absolute paths in the safe character set. Never the pool's slot root, <c>/</c>, <c>/root</c>, <c>/home</c>, <c>/var</c>, <c>/run</c>,
    /// <c>/etc</c>, <c>/proc</c>, <c>/sys</c>, <c>/dev</c> or <c>/tmp</c>, or a directory above or below any of them.
    /// </summary>
    public IList<string> ExtraReadOnlyBinds { get; set; } = [];

    /// <summary>The highest slot uid.</summary>
    public int MaxUid => SlotUidBase + MaxSessions - 1;
}

/// <summary>Checks <see cref="BrowserPoolOptions"/> at startup and again before any command line is built. Fails closed.</summary>
public sealed partial class BrowserPoolOptionsValidator : IValidateOptions<BrowserPoolOptions>
{
    /// <summary>Directories that must never be bound into a sandbox, or sit inside the slot root.</summary>
    private static readonly string[] _forbiddenBinds = ["/root", "/home", "/var", "/run", "/etc", "/proc", "/sys", "/dev", "/tmp", "/boot", "/mnt", "/media", "/srv"];

    /// <summary>First path segments the slot root may not live under: it is wiped with <c>rm -rf</c>.</summary>
    private static readonly string[] _forbiddenSlotRootTops = ["bin", "sbin", "lib", "lib32", "lib64", "usr", "etc", "boot", "proc", "sys", "dev", "root"];

    [GeneratedRegex(@"^/[A-Za-z0-9._+\-]+(/[A-Za-z0-9._+\-]+)*\z", RegexOptions.CultureInvariant)]
    private static partial Regex SafePathPattern();

    /// <summary>True when the path is absolute, uses only letters, digits and <c>. _ + -</c>, has no empty, <c>.</c> or <c>..</c> segment and no trailing slash.</summary>
    public static bool IsSafeAbsolutePath(string? path)
    {
        if (string.IsNullOrEmpty(path) || path.Length > 512 || !SafePathPattern().IsMatch(path))
        {
            return false;
        }

        foreach (var segment in path.Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            if (segment is "." or "..")
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>Returns every problem with the options; empty means valid.</summary>
    public static IReadOnlyList<string> Check(BrowserPoolOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        var errors = new List<string>();

        if (options.MaxSessions is < 1 or > 1000)
        {
            errors.Add("MaxSessions must be 1 to 1000.");
        }

        if (options.MaxSessionsPerAgent < 1 || options.MaxSessionsPerAgent > Math.Max(1, options.MaxSessions))
        {
            errors.Add("MaxSessionsPerAgent must be at least 1 and at most MaxSessions.");
        }

        if (options.SlotUidBase < 10000)
        {
            errors.Add("SlotUidBase must be at least 10000 (below that are system and login users).");
        }

        if ((long)options.SlotUidBase + options.MaxSessions > 59999)
        {
            errors.Add("SlotUidBase + MaxSessions must be at most 59999.");
        }

        CheckPath(errors, nameof(options.SlotRoot), options.SlotRoot);
        CheckPath(errors, nameof(options.ChromiumPath), options.ChromiumPath);
        CheckPath(errors, nameof(options.BwrapPath), options.BwrapPath);
        CheckPath(errors, nameof(options.SetprivPath), options.SetprivPath);
        CheckPath(errors, nameof(options.CagePath), options.CagePath);
        if (options.DiskMaxMb != 0)
        {
            CheckPath(errors, nameof(options.MountPath), options.MountPath);
            CheckPath(errors, nameof(options.UmountPath), options.UmountPath);
        }

        if (!Enum.IsDefined(options.NetworkIsolation))
        {
            errors.Add("NetworkIsolation is not a known value.");
        }
        else if (options.NetworkIsolation == NetworkIsolation.PerSlotNamespace)
        {
            CheckPath(errors, nameof(options.IpPath), options.IpPath);
            CheckPath(errors, nameof(options.NsenterPath), options.NsenterPath);
            CheckPath(errors, nameof(options.SysctlPath), options.SysctlPath);
            var netError = PoolNetwork.CheckBase(options);
            if (netError is not null)
            {
                errors.Add(netError);
            }

            var interfaceError = PoolNetwork.CheckEgressInterface(options);
            if (interfaceError is not null)
            {
                errors.Add(interfaceError);
            }
        }

        if (IsSafeAbsolutePath(options.SlotRoot))
        {
            var segments = options.SlotRoot.Split('/', StringSplitOptions.RemoveEmptyEntries);
            if (segments.Length < 2)
            {
                errors.Add("SlotRoot must have at least two path segments (not '/' or '/var').");
            }
            else if (Array.IndexOf(_forbiddenSlotRootTops, segments[0]) >= 0)
            {
                errors.Add($"SlotRoot may not be under /{segments[0]}.");
            }
        }

        if (options.SessionLifetime <= TimeSpan.Zero || options.SessionLifetime > TimeSpan.FromHours(24))
        {
            errors.Add("SessionLifetime must be above zero and at most 24 hours.");
        }

        if (options.IdleTimeout <= TimeSpan.Zero)
        {
            errors.Add("IdleTimeout must be above zero.");
        }

        if (options.OpenTimeout <= TimeSpan.Zero || options.OpenTimeout > TimeSpan.FromHours(1))
        {
            errors.Add("OpenTimeout must be above zero and at most one hour.");
        }

        if (options.MemoryMaxMb != 0 && options.MemoryMaxMb is < 128 or > 1_048_576)
        {
            errors.Add("MemoryMaxMb must be 0 (no limit) or 128 to 1048576.");
        }

        if (options.CpuQuotaPercent != 0 && options.CpuQuotaPercent is < 10 or > 100_000)
        {
            errors.Add("CpuQuotaPercent must be 0 (no limit) or 10 to 100000.");
        }

        if (options.TasksMax is < 64 or > 65_536)
        {
            errors.Add("TasksMax must be 64 to 65536.");
        }

        if (options.DiskMaxMb != 0 && options.DiskMaxMb is < 16 or > 65_536)
        {
            errors.Add("DiskMaxMb must be 0 (no cap) or 16 to 65536.");
        }

        if (options.DiskMaxMb != 0 && options.DiskMaxInodes is < 1024 or > 16_777_216)
        {
            errors.Add("DiskMaxInodes must be 1024 to 16777216 when DiskMaxMb is above 0.");
        }

        if (options.DiskMaxMb > 0 && options.MemoryMaxMb > 0 && options.UseSystemdRun && options.DiskMaxMb >= options.MemoryMaxMb)
        {
            errors.Add("DiskMaxMb must be smaller than MemoryMaxMb: tmpfs pages count toward the session's memory cap, so the disk cap could never be reached before the memory cap kills the session.");
        }

        CheckExtraBinds(errors, options);
        return errors;
    }

    /// <inheritdoc />
    public ValidateOptionsResult Validate(string? name, BrowserPoolOptions options)
    {
        var errors = Check(options);
        return errors.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(errors);
    }

    private static void CheckPath(List<string> errors, string name, string? value)
    {
        if (!IsSafeAbsolutePath(value))
        {
            errors.Add($"{name} must be an absolute path of letters, digits and . _ + - only, with no '..', spaces or trailing slash.");
        }
    }

    private static void CheckExtraBinds(List<string> errors, BrowserPoolOptions options)
    {
        if (options.ExtraReadOnlyBinds is null)
        {
            errors.Add("ExtraReadOnlyBinds may not be null.");
            return;
        }

        foreach (var bind in options.ExtraReadOnlyBinds)
        {
            if (!IsSafeAbsolutePath(bind))
            {
                errors.Add("Every ExtraReadOnlyBinds entry must be a safe absolute path.");
                continue;
            }

            foreach (var forbidden in _forbiddenBinds)
            {
                if (Overlaps(bind, forbidden))
                {
                    errors.Add($"ExtraReadOnlyBinds entry '{bind}' overlaps {forbidden}, which is never bound into a sandbox.");
                }
            }

            if (IsSafeAbsolutePath(options.SlotRoot) && Overlaps(bind, options.SlotRoot))
            {
                errors.Add($"ExtraReadOnlyBinds entry '{bind}' overlaps SlotRoot, which would expose other slots.");
            }
        }
    }

    /// <summary>True when one path equals, contains or sits inside the other. Both are already normalised.</summary>
    private static bool Overlaps(string a, string b) =>
        a == b || a.StartsWith(b + "/", StringComparison.Ordinal) || b.StartsWith(a + "/", StringComparison.Ordinal);
}
