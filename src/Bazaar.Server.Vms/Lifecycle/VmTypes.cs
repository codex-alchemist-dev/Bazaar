using Bazaar.Server.Vms.Environments;

namespace Bazaar.Server.Vms.Lifecycle;

/// <summary>
/// A VM as the backend sees it. <see cref="VmId"/> is the environment id (<c>environment_vm.id</c>); <see cref="BackendRef"/>
/// is the backend's own handle, stored in <c>environment_vm.backend_ref</c>.
/// </summary>
public sealed record VmHandle(Guid VmId, string BackendRef);

/// <summary>A golden image version that a child disk is created from (<c>golden_image.id</c>).</summary>
public readonly record struct ImageVersionId(Guid Value);

/// <summary>A checkpoint the backend created for a VM (<c>environment_checkpoint.id</c>).</summary>
public readonly record struct CheckpointId(Guid Value);

/// <summary>Sizing and OS of a new child VM, from the <c>vm.windows.*</c> or <c>vm.linux.*</c> settings.</summary>
public sealed record VmSpec(VmOs Os, int Vcpu, int MemoryMb, int DiskCapGb);

/// <summary>How a running VM is stopped: a guest shutdown, or a power cut.</summary>
public enum StopMode
{
    Graceful,
    Force,
}
