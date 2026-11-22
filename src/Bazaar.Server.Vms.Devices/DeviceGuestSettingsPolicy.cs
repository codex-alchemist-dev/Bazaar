using Bazaar.Server.Settings;
using Bazaar.Server.Vms.Lifecycle;

namespace Bazaar.Server.Vms.Devices;

/// <summary>
/// <see cref="IDeviceGuestPolicy"/> over <c>vms.devices.max_concurrent</c> and <c>vms.devices.idle_stop_minutes</c> (doc 62 section
/// 9.3). Both keys are live: they are read from the settings store on every call, like <see cref="DeviceProvidersGate"/>, so a change
/// needs no restart. A store failure propagates, so a start or a sweep fails instead of guessing.
/// </summary>
public sealed class DeviceGuestSettingsPolicy(ISettingStore settings) : IDeviceGuestPolicy
{
    private readonly ISettingStore _settings = settings ?? throw new ArgumentNullException(nameof(settings));

    public async ValueTask<int> GetMaxConcurrentAsync(CancellationToken ct) =>
        await _settings.GetAsync(DeviceProvidersSettings.MaxConcurrent, cancellationToken: ct).ConfigureAwait(false);

    public async ValueTask<int> GetIdleStopMinutesAsync(CancellationToken ct) =>
        await _settings.GetAsync(DeviceProvidersSettings.IdleStopMinutes, cancellationToken: ct).ConfigureAwait(false);
}
