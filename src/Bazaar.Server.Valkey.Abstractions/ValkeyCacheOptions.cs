namespace Bazaar.Server.Valkey;

/// <summary>
/// Settings for the Valkey cache provider (doc 03 <c>host.provider.cache</c> = <c>valkey</c>). Bound from <see cref="SectionName"/>.
/// Set exactly one of <see cref="Endpoint"/> or <see cref="SecretReference"/>; the provider reports a configuration error otherwise.
/// When <see cref="Managed"/> is true and neither is set, the provider uses the loopback endpoint of the supervised child.
/// </summary>
public sealed class ValkeyCacheOptions
{
    /// <summary>Configuration section, read from <c>appsettings.json</c> or <c>BAZAAR__</c> environment variables.</summary>
    public const string SectionName = "Bazaar:Cache:Valkey";

    /// <summary>
    /// A plain settings value, for example <c>localhost:6379</c>. It must carry no password or TLS secret.
    /// </summary>
    public string? Endpoint { get; set; }

    /// <summary>
    /// A bootstrap secret reference (<c>env:</c>, <c>file:</c> or <c>systemd-creds:</c>) whose value is the full client connection
    /// string, password included. The value is resolved at connect time and never logged.
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

    /// <summary>TCP port the managed child listens on, bound to 127.0.0.1 only. The default avoids a stock Redis on 6379.</summary>
    public int ManagedPort { get; set; } = 16379;

    /// <summary>Memory use, as a percentage of the server limit, at which the dependencies probe turns amber (doc 42 section 11).</summary>
    public int MemoryWarnPercent { get; set; } = 80;
}
