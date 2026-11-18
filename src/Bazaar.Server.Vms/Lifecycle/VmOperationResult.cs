using Bazaar.Server.Vms.Environments;

namespace Bazaar.Server.Vms.Lifecycle;

/// <summary>Why a lifecycle operation was refused. A refusal is a normal result, not an exception.</summary>
public enum VmRefusalReason
{
    /// <summary>No VM row has that id.</summary>
    UnknownVm,

    /// <summary>The transition table forbids moving from the recorded state to the requested one. The backend was not called.</summary>
    ForbiddenTransition,

    /// <summary>The recorded state changed underneath the operation, so the compare-and-set failed.</summary>
    StateChanged,
}

/// <summary>A refused operation: the reason, the state that was recorded, and the state the operation asked for.</summary>
public sealed record VmRefusal(VmRefusalReason Reason, VmState? CurrentState, VmState? RequestedState);

/// <summary>The outcome of a lifecycle operation that changes state.</summary>
public abstract record VmOperationResult
{
    private VmOperationResult()
    {
    }

    /// <summary>The backend call succeeded and the new state is recorded.</summary>
    public sealed record Applied(VmState State) : VmOperationResult;

    /// <summary>The operation was not applied. The backend was called only if the reason is <see cref="VmRefusalReason.StateChanged"/>.</summary>
    public sealed record Refused(VmRefusal Refusal) : VmOperationResult;
}

/// <summary>The outcome of a checkpoint, which also returns the backend's checkpoint id.</summary>
public abstract record VmCheckpointResult
{
    private VmCheckpointResult()
    {
    }

    public sealed record Applied(CheckpointId Checkpoint) : VmCheckpointResult;

    public sealed record Refused(VmRefusal Refusal) : VmCheckpointResult;
}
