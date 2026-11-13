using System.Net;
using Bazaar.Conduit;

namespace Bazaar.Conduit.Tests.Overlay;

/// <summary>Overlay address source that returns a fixed address, or throws a fixed exception.</summary>
internal sealed class FakeOverlayAddressSource(IPAddress? address = null, Exception? error = null) : IOverlayAddressSource
{
    public int Calls { get; private set; }

    public Task<IPAddress?> GetHostOverlayAddressAsync(CancellationToken cancellationToken)
    {
        Calls++;
        if (error is not null)
        {
            throw error;
        }

        return Task.FromResult(address);
    }
}
