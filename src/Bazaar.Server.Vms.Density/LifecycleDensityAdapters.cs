using Bazaar.Server.VmDensity.Abstractions;
using Bazaar.Server.Vms.Lifecycle;

namespace Bazaar.Server.Vms.Density;

/// <summary>
/// <see cref="IDensityVmDirectory"/> over the VM lifecycle (doc 62 section 9.3): every VM whose row is Running, with save support from
/// its own backend and the lifecycle's activity (flags false and last use at the last start when nothing better is known). The
/// lifecycle is resolved on first use, because the lifecycle itself depends on the governor that depends on this directory.
/// </summary>
public sealed class LifecycleDensityVmDirectory(Func<VmLifecycleService> lifecycle) : IDensityVmDirectory
{
    private readonly Func<VmLifecycleService> _lifecycle = lifecycle ?? throw new ArgumentNullException(nameof(lifecycle));

    /// <inheritdoc />
    public async Task<IReadOnlyList<RunningVm>> ListRunningAsync(CancellationToken cancellationToken)
    {
        var running = await _lifecycle().ListRunningAsync(cancellationToken).ConfigureAwait(false);
        return [.. running.Select(r => new RunningVm(VmLifecycleService.ToDensityVm(r.Vm), r.Activity, r.CanSaveState))];
    }
}

/// <summary>
/// <see cref="IDensityVmControl"/> over the VM lifecycle (doc 62 section 9.3). The save goes through the lifecycle, which routes it to
/// the VM's own backend and records the Saved state. A refusal is thrown, so the governor records a failed save and skips the VM.
/// </summary>
public sealed class LifecycleDensityVmControl(Func<VmLifecycleService> lifecycle) : IDensityVmControl
{
    private readonly Func<VmLifecycleService> _lifecycle = lifecycle ?? throw new ArgumentNullException(nameof(lifecycle));

    /// <inheritdoc />
    public async Task SaveAsync(Guid vmId, CancellationToken cancellationToken)
    {
        var result = await _lifecycle().SaveAsync(vmId, cancellationToken).ConfigureAwait(false);
        if (result is VmOperationResult.Refused refused)
        {
            var r = refused.Refusal;
            throw new InvalidOperationException(
                $"VM {vmId} was not saved: {r.Reason} (recorded {r.CurrentState?.ToString() ?? "none"}){(r.Detail is null ? "" : ": " + r.Detail)}.");
        }
    }
}
