using Bazaar.Server.Vms.Abstractions;
using Bazaar.Server.Vms.Environments;

namespace Bazaar.Server.Vms.Ports;

/// <summary>Pure mapping from the VM module's records to the types other modules see (<c>Bazaar.Server.Vms.Abstractions</c>).</summary>
public static class AgentVmMapping
{
    /// <summary>Maps a guest OS.</summary>
    public static VmGuestOs ToGuestOs(VmOs os) => os switch
    {
        VmOs.Windows => VmGuestOs.Windows,
        VmOs.Linux => VmGuestOs.Linux,
        _ => throw new ArgumentOutOfRangeException(nameof(os), os, "Unknown guest OS."),
    };

    /// <summary>
    /// Maps a recorded state. Returns null for Retired, which other modules never see. Checkpointed and Restored map to
    /// <see cref="VmRunState.Other"/> because the recorded state does not say whether the VM is powered.
    /// </summary>
    public static VmRunState? ToRunState(VmState state) => state switch
    {
        VmState.Absent or VmState.Provisioning => VmRunState.Provisioning,
        VmState.Stopped => VmRunState.Stopped,
        VmState.Running => VmRunState.Running,
        VmState.Saved => VmRunState.Saved,
        VmState.Checkpointed or VmState.Restored => VmRunState.Other,
        VmState.Retired => null,
        _ => throw new ArgumentOutOfRangeException(nameof(state), state, "Unknown VM state."),
    };

    /// <summary>
    /// Maps a record, or returns null for a retired VM. A row recorded as Running without a backend reference breaks the contract that
    /// a running VM has one, so it is reported as <see cref="VmRunState.Other"/>, never as running.
    /// </summary>
    /// <param name="record">The VM record.</param>
    /// <param name="canSaveState">What the backend says about saving, or null when no backend is known.</param>
    public static AgentVmInfo? ToInfo(EnvironmentVmRecord record, bool? canSaveState = null)
    {
        ArgumentNullException.ThrowIfNull(record);
        if (ToRunState(record.State) is not { } state)
            return null;

        var backendRef = string.IsNullOrWhiteSpace(record.BackendRef) ? null : record.BackendRef;
        if (state == VmRunState.Running && backendRef is null)
            state = VmRunState.Other;
        return new AgentVmInfo(record.Id, record.AgentId, ToGuestOs(record.Os), backendRef, state, record.MemoryMb, canSaveState);
    }
}
