using System.Net;

namespace Bazaar.Conduit;

/// <summary>
/// Source of the host's address on an overlay network. The Tailscale module asks the Tailscale CLI; tests use a fake.
/// Doc 39 section 2, rows 40 and 41.
/// </summary>
public interface IOverlayAddressSource
{
    /// <summary>
    /// Returns the host's overlay IPv4 address, or null when the overlay is not running or its output is not an
    /// address. Throws when the source itself cannot run (for example the CLI is not installed).
    /// </summary>
    Task<IPAddress?> GetHostOverlayAddressAsync(CancellationToken cancellationToken);
}
