namespace Bazaar.Server.VmMounts;

/// <summary>Guest paths the server chooses for one guest OS. A mount request must name exactly these.</summary>
public sealed record GuestMountLayout(string SoulPath, string WorkspacePath, string ProfilePath)
{
    public static GuestMountLayout DefaultLinux { get; } = new("/mnt/bazaar/soul", "/mnt/bazaar/workspace", "/var/lib/bazaar/profile");

    public static GuestMountLayout DefaultWindows { get; } = new(@"C:\Bazaar\Soul", @"C:\Bazaar\Workspace", @"C:\Bazaar\Profile");
}

/// <summary>Bounds on a cookie profile in either direction. A profile over any bound is refused, never truncated.</summary>
public sealed record ProfileLimits(int MaxFiles, long MaxFileBytes, long MaxTotalBytes)
{
    public static ProfileLimits Default { get; } = new(20_000, 256L * 1024 * 1024, 1024L * 1024 * 1024);
}

/// <summary>
/// Settings of the scoped-mount module. The fixed floors (Soul read-only, workspace read-write, host paths inside the agent's own
/// root, no write-back after hard taint) are not options.
/// </summary>
public sealed class VmMountOptions
{
    /// <summary>Directory that holds one folder per agent, named by the agent id in <c>N</c> format. An agent's root is <c>AgentsRoot/agentId</c>.</summary>
    public required string AgentsRoot { get; init; }

    /// <summary>Extra absolute paths that may never be mounted, nor contain a mount (the audit store, the secrets store, key material).</summary>
    public IReadOnlyList<string> DenyPaths { get; init; } = [];

    /// <summary>Compare names case-insensitively. Defaults to the host OS rule. Set it for a case-insensitive volume on any OS.</summary>
    public bool CaseInsensitive { get; init; } = OperatingSystem.IsWindows();

    /// <summary>Folder of an agent's root that may hold Soul mounts. A Soul mount path must be this folder or below it.</summary>
    public string SoulFolderName { get; init; } = "soul";

    /// <summary>Folder of an agent's root that may hold workspace mounts.</summary>
    public string WorkspaceFolderName { get; init; } = "workspace";

    /// <summary>Folder inside a Soul mount that holds the browser cookie profile.</summary>
    public string ProfileFolderName { get; init; } = "browser-profile";

    /// <summary>Folder of an agent's root, outside every mount, where a guest profile is staged before it is swapped in.</summary>
    public string StagingFolderName { get; init; } = "staging";

    /// <summary>Entries examined in a mount tree at attach. A larger tree is refused (fail closed).</summary>
    public int MaxScanEntries { get; init; } = 500_000;

    /// <summary>Canary credentials planted in a Soul mount.</summary>
    public int CanaryCount { get; init; } = 2;

    /// <summary>Server-side limit on one guest operation (a whole profile push or pull, or a provider attach). The guest never sets it.</summary>
    public TimeSpan GuestIoTimeout { get; init; } = TimeSpan.FromMinutes(5);

    /// <summary>Limit on the host-side detach and the guest scrub of a containment detach, so a hung provider cannot stall the kill switch.</summary>
    public TimeSpan ContainmentTimeout { get; init; } = TimeSpan.FromSeconds(30);

    public ProfileLimits ProfileLimits { get; init; } = ProfileLimits.Default;

    public GuestMountLayout Linux { get; init; } = GuestMountLayout.DefaultLinux;

    public GuestMountLayout Windows { get; init; } = GuestMountLayout.DefaultWindows;
}
