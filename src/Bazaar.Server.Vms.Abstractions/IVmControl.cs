namespace Bazaar.Server.Vms.Abstractions;

/// <summary>How a running VM is stopped.</summary>
public enum VmStopMode
{
    /// <summary>A guest shutdown.</summary>
    Graceful,

    /// <summary>A power cut.</summary>
    Force,
}

/// <summary>Why a control request did or did not take effect. A refusal is a normal result, not an exception.</summary>
public enum VmControlOutcome
{
    /// <summary>The backend acted and the new state is recorded.</summary>
    Applied,

    /// <summary>No VM has that id. The backend was not called.</summary>
    UnknownVm,

    /// <summary>The VM's recorded state does not allow the request (for example saving a VM that is not running). The backend was not called.</summary>
    NotAllowedInState,

    /// <summary>The recorded state changed while the request ran, so the result was not recorded.</summary>
    StateChanged,
}

/// <summary>The result of a control request.</summary>
/// <param name="Outcome">What happened.</param>
/// <param name="State">
/// The recorded state afterwards: the new state when applied, the state that was found when refused, or null when the VM is unknown
/// or is retired.
/// </param>
public sealed record VmControlResult(VmControlOutcome Outcome, VmRunState? State)
{
    /// <summary>True when <see cref="Outcome"/> is <see cref="VmControlOutcome.Applied"/>.</summary>
    public bool IsApplied => Outcome == VmControlOutcome.Applied;
}

/// <summary>
/// Narrow control port for other modules: stop or save one VM by id. Both go through the VM module's lifecycle service, so the
/// transition table, the per-VM lock and the recorded state apply as for any other caller. A backend failure propagates as an exception
/// and leaves the recorded state unchanged.
/// </summary>
public interface IVmControl
{
    /// <summary>Saves a running VM's state and releases its memory.</summary>
    /// <param name="vmId">The VM.</param>
    /// <param name="ct">Cancellation.</param>
    Task<VmControlResult> SaveAsync(Guid vmId, CancellationToken ct);

    /// <summary>Stops a running VM.</summary>
    /// <param name="vmId">The VM.</param>
    /// <param name="mode">A guest shutdown or a power cut.</param>
    /// <param name="ct">Cancellation.</param>
    Task<VmControlResult> StopAsync(Guid vmId, VmStopMode mode, CancellationToken ct);
}
