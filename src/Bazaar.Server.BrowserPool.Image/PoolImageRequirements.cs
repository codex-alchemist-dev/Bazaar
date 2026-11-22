using Bazaar.Server.BrowserPool.LinuxHost;

namespace Bazaar.Server.BrowserPool.Image;

/// <summary>What kind of thing a pool VM must have.</summary>
public enum PoolImageRequirementKind
{
    /// <summary>An executable file, at an absolute path or a bare name found on the standard search path.</summary>
    Program,

    /// <summary>A directory.</summary>
    Directory,

    /// <summary>The AppArmor profile that lets <c>bwrap</c> create user namespaces. <see cref="PoolImageRequirement.Path"/> is where it is installed.</summary>
    AppArmorProfile,
}

/// <summary>One requirement of the pool VM image.</summary>
/// <param name="Id">Stable id, for example <c>program:bwrap</c>.</param>
/// <param name="Kind">What it is.</param>
/// <param name="Path">Where it must be, as configured.</param>
/// <param name="SlotsMustExecute">True when slot uids must be able to execute (programs) or traverse (directories) it.</param>
public sealed record PoolImageRequirement(string Id, PoolImageRequirementKind Kind, string Path, bool SlotsMustExecute);

/// <summary>
/// The exact list of what a pool VM must have for the configured pool to work (docs/design/59, section 7). Built from the pool's and
/// the Linux host's options, so it follows the configuration: the namespace tools only when the pool uses a namespace per slot, the
/// probe tools only when the probe is on, and so on. The order is fixed and no path appears twice.
/// </summary>
public sealed class PoolImageRequirements
{
    private PoolImageRequirements(IReadOnlyList<PoolImageRequirement> items) => Items = items;

    /// <summary>The requirements, in a fixed order.</summary>
    public IReadOnlyList<PoolImageRequirement> Items { get; }

    /// <summary>The bare program name used for the <c>systemd-run</c> scope in the launch argv.</summary>
    public const string SystemdRunProgram = "systemd-run";

    /// <summary>Derives the list. Options are assumed valid (the pool and the host validate them at startup).</summary>
    public static PoolImageRequirements From(BrowserPoolOptions pool, LinuxPoolHostOptions host)
    {
        ArgumentNullException.ThrowIfNull(pool);
        ArgumentNullException.ThrowIfNull(host);

        var list = new List<PoolImageRequirement>();
        var program = (string id, string path, bool slots) => list.Add(new(id, PoolImageRequirementKind.Program, path, slots));

        // Run by the slot uid inside the sandbox chain: chromium and bwrap, and cage for headed sessions.
        program("program:chromium", pool.ChromiumPath, true);
        program("program:bwrap", pool.BwrapPath, true);
        program("program:setpriv", pool.SetprivPath, false);
        program("program:cage", pool.CagePath, true);
        if (pool.NetworkIsolation == NetworkIsolation.PerSlotNamespace)
        {
            program("program:ip", pool.IpPath, false);
            program("program:nsenter", pool.NsenterPath, false);
            program("program:sysctl", pool.SysctlPath, false);
        }

        program("program:nft", host.NftPath, false);
        if (pool.UseSystemdRun)
        {
            program("program:systemd-run", SystemdRunProgram, false);
        }

        if (host.PoolSpawnHelperPath is { } helper)
        {
            program("program:pool-spawn", helper, false);
        }

        if (host.ProbeEnabled)
        {
            if ((host.ProbeToolPath ?? host.PoolSpawnHelperPath) is { } probe)
            {
                // The probe connects as the slot's uid, so the slot must be able to run it.
                program("program:probe-tool", probe, true);
            }

            program("program:probe-setpriv", host.SetprivPath, false);
            program("program:probe-nsenter", host.NsenterPath, false);
        }

        list.Add(new("directory:slot-root", PoolImageRequirementKind.Directory, pool.SlotRoot, true));
        if (host.PoolSpawnHelperPath is not null)
        {
            list.Add(new("directory:pipe-parent", PoolImageRequirementKind.Directory, PipeParent(host), false));
        }

        list.Add(new("apparmor:" + BwrapAppArmorProfile.ProfileName, PoolImageRequirementKind.AppArmorProfile, BwrapAppArmorProfile.InstallPath, false));
        return new PoolImageRequirements(Deduplicate(list));
    }

    /// <summary>
    /// The directory above the pipe directory, which the image creates root-only. It must be a safe path with at least two segments, so
    /// the image never changes the mode of a top-level directory such as <c>/run</c>.
    /// </summary>
    /// <exception cref="ArgumentException">The pipe directory is not usable for that.</exception>
    internal static string PipeParent(LinuxPoolHostOptions host)
    {
        string path = host.PipeDirectory;
        int cut = path?.LastIndexOf('/') ?? -1;
        string parent = cut > 0 ? path![..cut] : string.Empty;
        if (!BrowserPoolOptionsValidator.IsSafeAbsolutePath(path) || parent.Split('/', StringSplitOptions.RemoveEmptyEntries).Length < 2)
        {
            throw new ArgumentException("The pipe directory must be a safe absolute path with at least three segments.", nameof(host));
        }

        return parent;
    }

    /// <summary>Keeps the first entry for each kind and path; when a later one needs slot access, the kept one does too.</summary>
    private static List<PoolImageRequirement> Deduplicate(List<PoolImageRequirement> list)
    {
        var result = new List<PoolImageRequirement>();
        var index = new Dictionary<(PoolImageRequirementKind, string), int>();
        foreach (var item in list)
        {
            if (index.TryGetValue((item.Kind, item.Path), out int at))
            {
                if (item.SlotsMustExecute && !result[at].SlotsMustExecute)
                {
                    result[at] = result[at] with { SlotsMustExecute = true };
                }

                continue;
            }

            index[(item.Kind, item.Path)] = result.Count;
            result.Add(item);
        }

        return result;
    }
}
