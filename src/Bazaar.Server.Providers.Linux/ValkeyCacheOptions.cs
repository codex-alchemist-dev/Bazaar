namespace Bazaar.Server.Providers.Linux;

/// <summary>
/// Settings for the Valkey cache provider (doc 03 <c>host.provider.cache</c> = <c>valkey</c>). Bound from <see cref="SectionName"/>.
/// Set exactly one of <see cref="Endpoint"/> or <see cref="SecretReference"/>; the provider reports a configuration error otherwise.
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
}
