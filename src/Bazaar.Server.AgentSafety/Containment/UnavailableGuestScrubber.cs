using Bazaar.Server.VmMounts.Abstractions;

namespace Bazaar.Server.AgentSafety.Containment;

/// <summary><see cref="IGuestScrubber"/> for a host with no Vms lifecycle composed. It refuses, so a detach reports the guest as not scrubbed.</summary>
public sealed class UnavailableGuestScrubber : IGuestScrubber
{
    /// <inheritdoc />
    public Task ScrubAsync(GuestScrubRequest request, CancellationToken ct) =>
        throw new InvalidOperationException("No VM lifecycle is composed, so the guest cannot be scrubbed.");
}
