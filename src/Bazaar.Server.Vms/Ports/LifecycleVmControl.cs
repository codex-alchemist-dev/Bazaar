using Bazaar.Server.Vms.Abstractions;
using Bazaar.Server.Vms.Environments;
using Bazaar.Server.Vms.Lifecycle;

namespace Bazaar.Server.Vms.Ports;

/// <summary>
/// The production <see cref="IVmControl"/>: save and stop go through <see cref="VmLifecycleService"/>, so the transition table, the
/// per-VM lock and the recorded state apply. A backend exception propagates.
/// </summary>
public sealed class LifecycleVmControl(VmLifecycleService lifecycle) : IVmControl
{
    /// <inheritdoc />
    public async Task<VmControlResult> SaveAsync(Guid vmId, CancellationToken ct) =>
        Map(await lifecycle.SaveAsync(vmId, ct).ConfigureAwait(false));

    /// <inheritdoc />
    public async Task<VmControlResult> StopAsync(Guid vmId, VmStopMode mode, CancellationToken ct) =>
        Map(await lifecycle.StopAsync(vmId, ToStopMode(mode), ct).ConfigureAwait(false));

    private static StopMode ToStopMode(VmStopMode mode) => mode switch
    {
        VmStopMode.Graceful => StopMode.Graceful,
        VmStopMode.Force => StopMode.Force,
        _ => throw new ArgumentOutOfRangeException(nameof(mode), mode, "Unknown stop mode."),
    };

    /// <summary>Maps a lifecycle result to the cross-module result.</summary>
    private static VmControlResult Map(VmOperationResult result) => result switch
    {
        VmOperationResult.Applied applied => new VmControlResult(VmControlOutcome.Applied, AgentVmMapping.ToRunState(applied.State)),
        VmOperationResult.Refused refused => new VmControlResult(
            refused.Refusal.Reason switch
            {
                VmRefusalReason.UnknownVm => VmControlOutcome.UnknownVm,
                VmRefusalReason.ForbiddenTransition => VmControlOutcome.NotAllowedInState,
                VmRefusalReason.StateChanged => VmControlOutcome.StateChanged,
                _ => throw new ArgumentOutOfRangeException(nameof(result), refused.Refusal.Reason, "Unknown refusal reason."),
            },
            refused.Refusal.CurrentState is { } current ? AgentVmMapping.ToRunState(current) : null),
        _ => throw new ArgumentOutOfRangeException(nameof(result), result, "Unknown operation result."),
    };
}
