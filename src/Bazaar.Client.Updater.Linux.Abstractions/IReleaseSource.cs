using Bazaar.Contracts.Releases;

namespace Bazaar.Client.Updater.Linux;

/// <summary>
/// Where the updater reads releases from: the client's own company server, nowhere else (doc 37 sections 4a and 4b).
/// The feed is untrusted until the updater has checked the hash and the detached signature.
/// </summary>
public interface IReleaseSource
{
    /// <summary>The current manifest, or null when the server offers none (the client then stays on its build).</summary>
    /// <exception cref="IOException">The feed could not be read.</exception>
    Task<ReleaseManifest?> GetManifestAsync(CancellationToken ct);

    /// <summary>Opens one URL taken from the manifest (absolute https or relative to the manifest's own address).</summary>
    /// <exception cref="IOException">The file could not be fetched.</exception>
    Task<Stream> OpenAsync(string url, CancellationToken ct);
}
