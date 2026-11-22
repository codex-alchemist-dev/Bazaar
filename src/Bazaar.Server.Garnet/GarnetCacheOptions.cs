using System.Globalization;

namespace Bazaar.Server.Garnet;

/// <summary>
/// Settings for the Garnet cache provider (doc 03 <c>host.provider.cache</c> = <c>garnet</c>, Windows). Bound from <see cref="SectionName"/>.
/// Unmanaged: set exactly one of <see cref="Endpoint"/> or <see cref="SecretReference"/>. Managed (<see cref="Managed"/>): the host runs
/// its own loopback server with a generated password, so neither may be set.
/// </summary>
public sealed class GarnetCacheOptions
{
    /// <summary>Configuration section, read from <c>appsettings.json</c> or <c>BAZAAR__</c> environment variables.</summary>
    public const string SectionName = "Bazaar:Cache:Garnet";

    /// <summary>A plain settings value, for example <c>localhost:6379</c>. It must carry no password. Unmanaged only.</summary>
    public string? Endpoint { get; set; }

    /// <summary>
    /// A bootstrap secret reference (<c>env:</c>, <c>file:</c> or <c>systemd-creds:</c>) whose value is the full client connection
    /// string, password included. Resolved at connect time and never logged. Unmanaged only.
    /// </summary>
    public string? SecretReference { get; set; }

    /// <summary>Prefix added to every key, so the cache namespace does not collide with other users of the same server.</summary>
    public string KeyPrefix { get; set; } = "bazaar:cache:";

    /// <summary>Upper bound on the startup and status probe. A server that does not answer in time is reported as unusable.</summary>
    public TimeSpan ProbeTimeout { get; set; } = TimeSpan.FromSeconds(2);

    /// <summary>The window, in seconds (1 to 120), in which the first cache probe retries while a managed child starts.</summary>
    public int StartWindowSeconds { get; set; } = 30;

    /// <summary>
    /// When true, the host runs a hidden Garnet child on loopback and supervises it. Off by default: the operator installs Garnet and sets
    /// <see cref="BinaryPath"/>. The code never downloads a server binary.
    /// </summary>
    public bool Managed { get; set; }

    /// <summary>The Garnet executable for a managed child. A bare name is looked up on the PATH.</summary>
    public string BinaryPath { get; set; } = "garnet-server.exe";

    /// <summary>The loopback TCP port of the managed child. 0 picks a free loopback port at each start (the default).</summary>
    public int ManagedPort { get; set; }

    /// <summary>
    /// Garnet's log memory size as a Garnet size string (<c>256m</c>, <c>1g</c>, or plain bytes). Passed as <c>--memory</c>; Garnet rounds
    /// it down to a power of two. Default 256m.
    /// </summary>
    public string MaxMemory { get; set; } = "256m";

    /// <summary>Memory use, as a percentage of <see cref="MaxMemory"/>, at which the dependencies probe turns amber (managed only).</summary>
    public int MemoryWarnPercent { get; set; } = 80;

    /// <summary>
    /// When true the child waits for each append-only-file commit before it answers (<c>--aof-commit-wait</c>). Off: a cache does not need
    /// the latency.
    /// </summary>
    public bool AofCommitWait { get; set; }

    /// <summary>
    /// Directory for the managed child's append-only file and checkpoints (<c>--checkpointdir</c>; <c>--logdir</c> is only valid with <c>--storage-tier</c>, so it is not passed) and its pid file. The
    /// password is never written here.
    /// </summary>
    public string DataDirectory { get; set; } = Path.Combine(AppContext.BaseDirectory, "garnet-data");

    /// <summary>Returns the first problem with the option values themselves, or null. The endpoint rule is checked by the provider.</summary>
    public string? ValidationError()
    {
        if (ProbeTimeout <= TimeSpan.Zero)
        {
            return "ProbeTimeout must be positive";
        }

        if (StartWindowSeconds is < 1 or > 120)
        {
            return "StartWindowSeconds must be between 1 and 120";
        }

        if (string.IsNullOrEmpty(KeyPrefix))
        {
            return "KeyPrefix must not be empty";
        }

        if (!Managed)
        {
            return null;
        }

        if (string.IsNullOrWhiteSpace(BinaryPath))
        {
            return "BinaryPath must be set for a managed server";
        }

        if (string.IsNullOrWhiteSpace(DataDirectory))
        {
            return "DataDirectory must be set for a managed server";
        }

        if (ManagedPort is < 0 or > 65535)
        {
            return "ManagedPort must be between 0 and 65535";
        }

        if (MemoryWarnPercent is < 1 or > 100)
        {
            return "MemoryWarnPercent must be between 1 and 100";
        }

        return GarnetSize.TryParseBytes(MaxMemory, out _) ? null : "MaxMemory must be a size such as 256m or 1g";
    }
}

/// <summary>Parses Garnet size strings (<c>512k</c>, <c>256m</c>, <c>1g</c>, <c>2t</c>, or plain bytes).</summary>
public static class GarnetSize
{
    /// <summary>Returns false for empty, zero, negative, overflowing or unrecognised input.</summary>
    public static bool TryParseBytes(string? text, out long bytes)
    {
        bytes = 0;
        var value = text?.Trim().ToLowerInvariant();
        if (string.IsNullOrEmpty(value))
        {
            return false;
        }

        if (value.EndsWith('b'))
        {
            value = value[..^1];
        }

        long multiplier = 1;
        if (value.Length > 0 && "kmgt".Contains(value[^1], StringComparison.Ordinal))
        {
            multiplier = value[^1] switch
            {
                'k' => 1L << 10,
                'm' => 1L << 20,
                'g' => 1L << 30,
                _ => 1L << 40,
            };
            value = value[..^1];
        }

        if (!long.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var number) || number <= 0
            || number > long.MaxValue / multiplier)
        {
            return false;
        }

        bytes = number * multiplier;
        return true;
    }
}
