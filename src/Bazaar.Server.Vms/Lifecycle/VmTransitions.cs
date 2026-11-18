using Bazaar.Server.Vms.Environments;

namespace Bazaar.Server.Vms.Lifecycle;

/// <summary>
/// The VM lifecycle transition table (doc 16, "Lifecycle"). It is pure and total: every pair of states gets an answer,
/// and anything not listed is forbidden.
/// </summary>
public static class VmTransitions
{
    public static bool CanTransition(VmState from, VmState to) => (from, to) switch
    {
        // Create: the child is provisioned, then either completes (Stopped) or fails (Absent).
        (VmState.Absent, VmState.Provisioning) => true,
        (VmState.Provisioning, VmState.Stopped) => true,
        (VmState.Provisioning, VmState.Absent) => true,

        // Power: start and graceful or forced stop.
        (VmState.Stopped, VmState.Running) => true,
        (VmState.Running, VmState.Stopped) => true,

        // Save: idle save or explicit save; resume from saved.
        (VmState.Running, VmState.Saved) => true,
        (VmState.Saved, VmState.Running) => true,

        // Checkpoint: only from a stopped or saved VM.
        (VmState.Stopped, VmState.Checkpointed) => true,
        (VmState.Saved, VmState.Checkpointed) => true,
        (VmState.Checkpointed, VmState.Restored) => true,

        // Restored: the backend decides the power state after a restore, so both Running and Stopped are allowed.
        // Refusing either would block a backend that resumes a saved checkpoint or one that leaves the VM off.
        (VmState.Restored, VmState.Running) => true,
        (VmState.Restored, VmState.Stopped) => true,

        // Retire: from any state that is not running and not being provisioned. Soul data is kept by the caller.
        (VmState.Stopped, VmState.Retired) => true,
        (VmState.Saved, VmState.Retired) => true,
        (VmState.Checkpointed, VmState.Retired) => true,
        (VmState.Restored, VmState.Retired) => true,

        _ => false,
    };
}
