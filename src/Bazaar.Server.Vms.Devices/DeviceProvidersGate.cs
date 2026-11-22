using Bazaar.Server.Settings;
using Bazaar.Server.Vms.Lifecycle;

namespace Bazaar.Server.Vms.Devices;

/// <summary>
/// <see cref="IVmProviderGate"/> over <c>vms.devices.enabled</c> (doc 62 sections 7 and 9.2). The list is read on every question, so
/// the router and the listing agree. A store failure propagates, so the create fails instead of guessing.
/// </summary>
public sealed class DeviceProvidersGate(ISettingStore settings) : IVmProviderGate
{
    private readonly ISettingStore _settings = settings ?? throw new ArgumentNullException(nameof(settings));

    public async ValueTask<bool> IsEnabledAsync(string kind, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(kind);
        var enabled = await _settings.GetAsync(DeviceProvidersSettings.Enabled, cancellationToken: ct).ConfigureAwait(false);
        return enabled.Contains(kind, StringComparer.Ordinal);
    }
}
