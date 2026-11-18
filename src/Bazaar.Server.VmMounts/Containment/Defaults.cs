using Bazaar.Server.VmMounts.Abstractions;

namespace Bazaar.Server.VmMounts.Containment;

/// <summary>
/// The shipped <see cref="IGuestScrubber"/>: it refuses. A containment detach that reaches it still detaches the mount, then reports
/// that the guest could not be scrubbed. Replace it with an implementation over the Vms lifecycle (pause, then revert the disk).
/// </summary>
public sealed class RefusingGuestScrubber : IGuestScrubber
{
    public Task ScrubAsync(GuestScrubRequest request, CancellationToken ct) =>
        throw new InvalidOperationException(
            "No IGuestScrubber is wired: the guest still holds its profile copy. Register an implementation over the Vms lifecycle.");
}

/// <summary>The shipped <see cref="IHardLinkProbe"/>: it cannot tell, so hard links are not detected until a provider supplies a probe.</summary>
public sealed class NullHardLinkProbe : IHardLinkProbe
{
    public int? GetLinkCount(string filePath) => null;
}
