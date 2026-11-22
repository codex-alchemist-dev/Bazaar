using Bazaar.Updates;

namespace Bazaar.Client.Updates;

/// <summary>
/// Supplies the signed manifest (version, SHA-256, signature) for a release the feed offered. The Velopack feed is untrusted
/// until the downloaded package matches this manifest (<see cref="UpdateVerifier"/>).
/// </summary>
public interface IClientUpdateManifestSource
{
    /// <summary>The manifest for the channel's latest release, or null when the company server has none.</summary>
    Task<UpdateManifest?> GetManifestAsync(UpdateChannel channel, CancellationToken ct);
}

/// <summary>The manifest source backed by the company server's <c>updates/latest</c> feed (<see cref="UpdateFeedClient"/>).</summary>
public sealed class UpdateFeedManifestSource(UpdateFeedClient feed) : IClientUpdateManifestSource
{
    private readonly UpdateFeedClient _feed = feed ?? throw new ArgumentNullException(nameof(feed));

    // The route carries no channel yet (doc 37 section 4b); the company server answers for the install's channel.
    public Task<UpdateManifest?> GetManifestAsync(UpdateChannel channel, CancellationToken ct) => _feed.GetLatestAsync(ct);
}
