using Bazaar.Conduit;

namespace Bazaar.Conduit.Tests.ManualPortForward;

/// <summary>Outside probe that returns a fixed answer or throws, and records its calls.</summary>
internal sealed class FakeOutsideProbe(bool result = false, Exception? exception = null) : IOutsideProbe
{
    public List<(Uri Probe, string KeyHash)> Calls { get; } = [];

    public Task<bool> ProbeAsync(Uri probe, string expectedKeyHash, CancellationToken cancellationToken)
    {
        Calls.Add((probe, expectedKeyHash));
        if (exception is not null)
        {
            return Task.FromException<bool>(exception);
        }

        return Task.FromResult(result);
    }
}
