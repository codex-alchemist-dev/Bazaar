namespace Bazaar.Server.Vms.Abstractions;

/// <summary>The guest operating system of an agent's VM.</summary>
public enum VmGuestOs
{
    /// <summary>A Windows guest.</summary>
    Windows,

    /// <summary>A Linux guest.</summary>
    Linux,
}

/// <summary>
/// What other modules may know about a VM's power state. It is the VM module's recorded lifecycle state (doc 16) cut down to the cases
/// a reader acts on. A retired VM has no value here: the directory never lists one.
/// </summary>
public enum VmRunState
{
    /// <summary>The VM is being created, or its row exists but the backend has no VM yet. It cannot be used.</summary>
    Provisioning,

    /// <summary>The VM is powered off.</summary>
    Stopped,

    /// <summary>The VM is running. <see cref="AgentVmInfo.BackendRef"/> is set.</summary>
    Running,

    /// <summary>The VM's state is saved and its memory released. Starting it resumes it.</summary>
    Saved,

    /// <summary>
    /// The power state is not known from the record: the VM is at a checkpoint or was just restored from one (the backend decides
    /// what power state a restore leaves), or a row says Running but holds no backend reference. Never treat it as running.
    /// </summary>
    Other,
}

/// <summary>One non-retired VM of an agent, as the VM module records it.</summary>
/// <param name="VmId">The VM's id (<c>environment_vm.id</c>).</param>
/// <param name="AgentId">The owning agent's id.</param>
/// <param name="Os">The guest OS.</param>
/// <param name="BackendRef">
/// The backend's own handle for the VM (the Hyper-V VM name or the libvirt domain name). Never null when <paramref name="State"/> is
/// <see cref="VmRunState.Running"/>; it may be null in other states.
/// </param>
/// <param name="State">The recorded power state.</param>
/// <param name="MemoryMb">
/// The memory assigned to the VM in MiB, from the VM's environment record. Zero or less means it is not known. Added after the first
/// five members (PROPOSED, doc 59 section 8); existing callers that pass five arguments keep compiling.
/// </param>
/// <param name="CanSaveState">
/// Whether the VM module's backend can save a running VM's state and resume it (the backend's save capability), or null when that is not
/// known (no backend is configured, or the directory was built without one). Null must be read as "cannot save". It says nothing about
/// whether saving is wise right now.
/// </param>
public sealed record AgentVmInfo(
    Guid VmId,
    Guid AgentId,
    VmGuestOs Os,
    string? BackendRef,
    VmRunState State,
    int MemoryMb = 0,
    bool? CanSaveState = null);
