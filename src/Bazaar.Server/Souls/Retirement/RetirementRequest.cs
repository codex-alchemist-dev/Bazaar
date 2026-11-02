using Bazaar.Contracts.Soul;

namespace Bazaar.Server.Souls.Retirement;

/// <summary>The Soul and agent being retired. Passport and token revocation are keyed by this pair.</summary>
/// <param name="SoulId">The Soul whose Passport entries are revoked. Its version chain is never touched.</param>
/// <param name="AgentId">The agent whose tokens and VM disks are retired.</param>
public sealed record SoulRetirementSubject(Guid SoulId, Guid AgentId);

/// <summary>
/// The agent's VM child disk (docs/design/16-agent-vms-and-environments.md). The child is stopped at once and deleted
/// after <see cref="RetainUntil"/>.
/// </summary>
/// <param name="VmId">The environment VM row (<c>environment_vm.id</c>).</param>
/// <param name="BackendRef">The hypervisor's identifier for the child disk.</param>
/// <param name="ParentImageId">The golden image the child was cut from. Never deleted by this run.</param>
/// <param name="RetainUntil">The earliest time the child disk may be deleted. Supplied by the caller from the retention setting.</param>
public sealed record VmChildDisk(Guid VmId, string BackendRef, string ParentImageId, DateTimeOffset RetainUntil);

/// <summary>
/// A golden-image parent disk that a retirement may consider deleting (docs/design/16, "Golden image and rebase":
/// the parent is kept until its last child retires).
/// </summary>
/// <param name="ImageId">The golden image row (<c>golden_image.id</c>).</param>
/// <param name="BackendRef">The hypervisor's identifier for the parent disk.</param>
/// <param name="OtherLiveChildren">Live child disks of this parent other than the child being retired in this run.</param>
/// <param name="ImageRetired">True only when the golden image is in the Retired state. A Ready image is never deleted.</param>
public sealed record VmParentDisk(string ImageId, string BackendRef, int OtherLiveChildren, bool ImageRetired);

/// <summary>What one retirement run acts on. A null disk means the agent had no VM of that kind, so no VM steps run.</summary>
/// <param name="Subject">The Soul and agent.</param>
/// <param name="ChildDisk">The agent's VM child disk, if it has one.</param>
/// <param name="ParentDisk">A parent disk to consider deleting after the child, if the caller wants that.</param>
public sealed record RetirementRequest(SoulRetirementSubject Subject, VmChildDisk? ChildDisk = null, VmParentDisk? ParentDisk = null);
