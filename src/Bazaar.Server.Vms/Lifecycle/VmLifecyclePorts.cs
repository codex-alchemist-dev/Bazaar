using Bazaar.Server.VmDensity.Abstractions;
using Bazaar.Server.Vms.Environments;

namespace Bazaar.Server.Vms.Lifecycle;

/// <summary>
/// The live device-guest limits of doc 62 section 9.3 (<c>vms.devices.max_concurrent</c> and <c>vms.devices.idle_stop_minutes</c>).
/// The device providers module implements it over the settings store and reads the keys on every call, so a change needs no restart.
/// </summary>
public interface IDeviceGuestPolicy
{
    /// <summary>How many device guests of all providers together may run at once. Zero or less means no aggregate cap.</summary>
    ValueTask<int> GetMaxConcurrentAsync(CancellationToken ct);

    /// <summary>Minutes an unused device guest may idle before the sweep stops it. Zero or less disables the sweep.</summary>
    ValueTask<int> GetIdleStopMinutesAsync(CancellationToken ct);
}

/// <summary>
/// What is happening in a VM now, from whichever module tracks it (exec, terminal, browser). Optional: without one, every flag is
/// false and the last use is the row's last state change (doc 62 section 9.3).
/// </summary>
public interface IVmActivitySource
{
    /// <summary>The VM's activity, or null when nothing is known about it.</summary>
    ValueTask<VmActivity?> GetAsync(Guid vmId, CancellationToken ct);
}

/// <summary>A VM whose row is Running, with what the density governor and the idle-stop sweep need to know about it.</summary>
/// <param name="Vm">The row.</param>
/// <param name="Kind">The provider kind that owns it, or null for a single-backend lifecycle.</param>
/// <param name="IsDeviceGuest">True when its provider is on-demand (doc 62 section 9.3).</param>
/// <param name="CanSaveState">True when its own backend can save its state.</param>
/// <param name="Activity">Its activity, with defaults where nothing is known.</param>
public sealed record RunningVmInfo(EnvironmentVmRecord Vm, string? Kind, bool IsDeviceGuest, bool CanSaveState, VmActivity Activity);
