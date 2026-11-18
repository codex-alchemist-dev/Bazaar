using Bazaar.Server.VmMounts.Abstractions;
using Bazaar.Server.Vms.Environments;
using Bazaar.Server.Vms.Lifecycle;

namespace Bazaar.Server.AgentSafety.Containment;

/// <summary>
/// The real <see cref="IGuestScrubber"/>, over the Vms lifecycle. A guest that held a mount's profile copy cannot be trusted after
/// a hard taint or kill switch, so the VM is paused first (a forced stop, which cuts it off) and then its disk is discarded by
/// retiring it. A fresh VM is provisioned separately from the parent image.
/// </summary>
/// <remarks>
/// Idempotent: the mount manager scrubs once per mount, so a second call for the same VM finds it retired and does nothing. A refused
/// step throws and leaves the VM paused. Reverting to a known-clean checkpoint instead of retiring needs a lookup of the VM's clean
/// baseline, which the Vms module does not expose yet, so retire is the only discard.
/// </remarks>
public sealed class VmGuestScrubber(VmLifecycleService lifecycle) : IGuestScrubber
{
    /// <inheritdoc />
    public async Task ScrubAsync(GuestScrubRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        var state = await lifecycle.GetStateAsync(request.VmId, ct).ConfigureAwait(false)
            ?? throw new InvalidOperationException($"VM {request.VmId} is unknown, so its guest cannot be scrubbed.");

        switch (state)
        {
            case VmState.Retired:
            case VmState.Absent:
                return;
            case VmState.Provisioning:
                throw new InvalidOperationException($"VM {request.VmId} is still provisioning and cannot be scrubbed yet.");
            case VmState.Running:
                Require(request.VmId, "paused", await lifecycle.StopAsync(request.VmId, StopMode.Force, ct).ConfigureAwait(false));
                break;
        }

        // Stopped, Saved, Checkpointed and Restored can all retire. A refusal (including a state change underneath) throws.
        Require(request.VmId, "discarded", await lifecycle.RetireAsync(request.VmId, ct).ConfigureAwait(false));
    }

    private static void Require(Guid vmId, string what, VmOperationResult result)
    {
        if (result is VmOperationResult.Refused refused)
        {
            throw new InvalidOperationException($"VM {vmId} could not be {what}: {refused.Refusal.Reason}.");
        }
    }
}
