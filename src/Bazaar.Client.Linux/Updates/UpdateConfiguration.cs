using Bazaar.Client.Updater.Linux;

namespace Bazaar.Client.Linux.Updates;

/// <summary>
/// The result of reading the update settings. Either updates are on (every field set) or they are off with a reason.
/// Updates are off unless a manifest address, a server key pin and a signing key are all present and well formed.
/// </summary>
public sealed record UpdateConfiguration
{
    /// <summary>True when every setting the updater needs is present and valid.</summary>
    public bool Enabled => DisabledReason is null;

    /// <summary>Why updates are off, or null when they are on.</summary>
    public string? DisabledReason { get; init; }

    /// <summary>The updater settings. Set when enabled.</summary>
    public LinuxUpdaterSettings? Settings { get; init; }

    /// <summary>The company server's update feed (<c>GET /updates/latest</c>). Set when enabled.</summary>
    public Uri? ManifestUri { get; init; }

    /// <summary>The server key-hash pin for the feed's https connection, or null on loopback http. Set when enabled.</summary>
    public string? ServerKeyHashPin { get; init; }

    /// <summary>Updates off, for <paramref name="reason"/>.</summary>
    public static UpdateConfiguration Off(string reason) => new() { DisabledReason = reason };
}
