namespace Bazaar.Server.Valkey;

/// <summary>
/// Settings for the Valkey cache provider (doc 03 <c>host.provider.cache</c> = <c>valkey</c>). Bound from <see cref="SectionName"/>.
/// Unmanaged: set exactly one of <see cref="Endpoint"/> or <see cref="SecretReference"/>. Managed (<see cref="Managed"/>): the host runs
/// its own loopback server with a generated password, so neither may be set.
/// </summary>
public sealed class ValkeyCacheOptions
{
    /// <summary>The eviction policy a managed server runs with. Fixed: it is the cache policy, not a setting.</summary>
    public const string EvictionPolicy = "allkeys-lru";

    /// <summary>Configuration section, read from <c>appsettings.json</c> or <c>BAZAAR__</c> environment variables.</summary>
    public const string SectionName = "Bazaar:Cache:Valkey";

    /// <summary>
    /// A plain settings value, for example <c>localhost:6379</c>. It must carry no password or TLS secret. Unmanaged only.
    /// </summary>
    public string? Endpoint { get; set; }

    /// <summary>
    /// A bootstrap secret reference (<c>env:</c>, <c>file:</c> or <c>systemd-creds:</c>) whose value is the full client connection
    /// string, password included. The value is resolved at connect time and never logged. Unmanaged only.
    /// </summary>
    public string? SecretReference { get; set; }

    /// <summary>Prefix added to every key, so the cache namespace does not collide with other users of the same server.</summary>
    public string KeyPrefix { get; set; } = "bazaar:cache:";

    /// <summary>Upper bound on the startup and status probe. A server that does not answer in time is reported as unusable.</summary>
    public TimeSpan ProbeTimeout { get; set; } = TimeSpan.FromSeconds(2);

    /// <summary>
    /// When true, the host runs a hidden <c>valkey-server</c> child on loopback and supervises it (doc 42 section 6). Off by default:
    /// the operator must install Valkey and set <see cref="BinaryPath"/>. The code never downloads a server binary.
    /// </summary>
    public bool Managed { get; set; }

    /// <summary>The <c>valkey-server</c> executable for a managed child. A bare name is looked up on the PATH.</summary>
    public string BinaryPath { get; set; } = "valkey-server";

    /// <summary>
    /// The loopback TCP port the managed child listens on. 0 picks a free loopback port at each start (the default); the chosen port is
    /// never reported as an endpoint, because clients take the port from the supervisor.
    /// </summary>
    public int ManagedPort { get; set; }

    /// <summary>Memory cap of a managed server, in megabytes. Written to the child's configuration as <c>maxmemory</c>.</summary>
    public int MaxMemoryMegabytes { get; set; } = 256;

    /// <summary>Memory use, as a percentage of the server limit, at which the dependencies probe turns amber (doc 40 section 4b.2).</summary>
    public int MemoryWarnPercent { get; set; } = 80;

    /// <summary>
    /// Directory for the managed child's pid file and its runtime state. Created with owner-only permissions on Linux. The password is
    /// never written here.
    /// </summary>
    public string RuntimeDirectory { get; set; } = Path.Combine(AppContext.BaseDirectory, "valkey-runtime");

    /// <summary>
    /// The wrapper that sets the parent-death signal (<c>setpriv --pdeathsig SIGKILL --</c> from util-linux). Empty runs the server
    /// without one, which the startup does not allow: the supervisor treats a missing wrapper as a failed start.
    /// </summary>
    public string ParentDeathWrapper { get; set; } = "setpriv";
}
