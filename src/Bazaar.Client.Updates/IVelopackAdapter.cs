namespace Bazaar.Client.Updates;

/// <summary>
/// The thin seam over the Velopack library. Everything Bazaar decides (channel, decision rules, verification) sits
/// above it, so tests use a fake and never touch the network or an installed app. It is stateful: a check remembers the
/// offer that the next download and apply act on.
/// </summary>
public interface IVelopackAdapter
{
    /// <summary>The running version, or null when this process is not a Velopack install.</summary>
    string? InstalledVersion { get; }

    /// <summary>Checks the feed on a Velopack channel. Returns the offered version, or null when there is none.</summary>
    Task<string?> CheckAsync(Uri feed, string velopackChannel, CancellationToken ct);

    /// <summary>Downloads the remembered offer and returns the local path of the package file.</summary>
    Task<string> DownloadAsync(IProgress<int>? progress, CancellationToken ct);

    /// <summary>Exits and restarts into the downloaded release.</summary>
    void ApplyAndRestart();
}
