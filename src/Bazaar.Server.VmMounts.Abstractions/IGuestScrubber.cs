namespace Bazaar.Server.VmMounts.Abstractions;

/// <summary>Why a guest is being scrubbed, and which mount's detach asked.</summary>
public sealed record GuestScrubRequest(Guid AgentId, Guid VmId, Guid MountId, DetachReason Reason);

/// <summary>
/// Quarantines a guest after a hard-taint or kill-switch detach. A detach removes host-side access, but the guest still holds its
/// working copy of the cookie profile. "Scrub" means: pause the VM and discard or revert its disk host-side, so the copy cannot be
/// used or exfiltrated later.
/// </summary>
/// <remarks>
/// The host must wire this to the Vms lifecycle (pause through <c>IVmBackend</c>, then restore the last clean checkpoint or recreate
/// the child disk). It must be idempotent: a VM with two mounts is asked once per mount. The shipped default,
/// <c>RefusingGuestScrubber</c>, throws, so an install that has not wired it fails visibly: the mount is still detached, but the
/// detach reports the failure and the audit log records <c>vm.mount.scrub_failed</c>.
/// </remarks>
public interface IGuestScrubber
{
    Task ScrubAsync(GuestScrubRequest request, CancellationToken ct);
}
