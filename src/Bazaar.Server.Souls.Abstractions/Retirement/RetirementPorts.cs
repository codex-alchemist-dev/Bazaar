using Bazaar.Contracts.Soul;

namespace Bazaar.Server.Souls.Retirement;

/// <summary>
/// Revokes the Soul's Passport entries for the subject (docs/design/14, "Kill switch"). Implementations must be idempotent:
/// returns <see cref="RetirementStepStatus.AlreadyDone"/> when nothing is left to revoke. Throws on a real failure.
/// </summary>
public interface IPassportRevoker
{
    /// <summary>Revokes; returns <see cref="RetirementStepStatus.Done"/> or <see cref="RetirementStepStatus.AlreadyDone"/>.</summary>
    Task<RetirementStepStatus> RevokeAsync(SoulRetirementSubject subject, CancellationToken cancellationToken);
}

/// <summary>
/// Revokes the agent's tokens and credential leases (docs/design/14, "Kill switch": revoke all credential leases and App
/// tokens). Idempotent, like <see cref="IPassportRevoker"/>.
/// </summary>
public interface ITokenRevoker
{
    /// <summary>Revokes; returns <see cref="RetirementStepStatus.Done"/> or <see cref="RetirementStepStatus.AlreadyDone"/>.</summary>
    Task<RetirementStepStatus> RevokeAsync(SoulRetirementSubject subject, CancellationToken cancellationToken);
}

/// <summary>
/// Stops and deletes VM disks (docs/design/16, "Retire"). Each method is idempotent and returns
/// <see cref="RetirementStepStatus.Done"/> or <see cref="RetirementStepStatus.AlreadyDone"/>; it throws on a real failure.
/// The coordinator enforces retention and parent-with-children rules before calling <see cref="DeleteParentDiskAsync"/>
/// or <see cref="DeleteChildDiskAsync"/>. Implementations must also refuse a parent with children on their own side.
/// </summary>
public interface IVmDiskRetirer
{
    /// <summary>Stops the child VM. Safe to call on a stopped VM.</summary>
    Task<RetirementStepStatus> StopAsync(VmChildDisk disk, CancellationToken cancellationToken);

    /// <summary>Deletes the child disk. Only called after retention has elapsed.</summary>
    Task<RetirementStepStatus> DeleteChildDiskAsync(VmChildDisk disk, CancellationToken cancellationToken);

    /// <summary>Deletes the parent disk. Only called when it has no other live children and its image is retired.</summary>
    Task<RetirementStepStatus> DeleteParentDiskAsync(VmParentDisk disk, CancellationToken cancellationToken);
}
