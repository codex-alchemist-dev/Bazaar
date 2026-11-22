using Bazaar.Contracts.Vms;
using Bazaar.Contracts.Wire;
using Bazaar.Contracts.Wire.Codec;
using Bazaar.Server.Settings;
using Bazaar.Server.Wire.Connection;

namespace Bazaar.Server.Vms.Devices;

/// <summary>
/// <c>vms.devices.list</c> (doc 62 section 9.1): the owner reads which device providers are enabled, their capability reports and
/// probe results, and the device guests that run now. Read-only. Only the owner principal may ask; any other principal gets
/// <c>not_found</c>, so a company's device usage is not disclosed. Only providers listed in <c>vms.devices.enabled</c> appear, and
/// a listed kind whose module is not installed is left out. The reply carries no agent name, port, path or screen content.
/// </summary>
public sealed class DeviceListWireHandler : IWireRequestHandler
{
    private readonly ISettingStore _settings;
    private readonly IEnumerable<IDeviceProviderCatalogEntry> _entries;

    /// <param name="settings">Where <c>vms.devices.enabled</c> is read.</param>
    /// <param name="entries">The providers that report themselves; the handler only reads them.</param>
    public DeviceListWireHandler(ISettingStore settings, IEnumerable<IDeviceProviderCatalogEntry> entries)
    {
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _entries = entries ?? throw new ArgumentNullException(nameof(entries));
    }

    /// <inheritdoc />
    public string Type => WireTypes.DeviceList;

    /// <inheritdoc />
    public async ValueTask<WireReply> HandleAsync(WireRequestContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);

        if (context.Connection.Principal.Kind != "owner")
        {
            return WireReply.Fail(WireErrorCodes.NotFound);
        }

        try
        {
            _ = context.Payload<DeviceListRequest>();
        }
        catch (WireFormatException)
        {
            return WireReply.Fail(WireErrorCodes.BadRequest, "The device list request does not fit its schema.");
        }

        var enabled = await _settings.GetAsync(DeviceProvidersSettings.Enabled, cancellationToken: cancellationToken).ConfigureAwait(false);

        var byKind = new Dictionary<string, IDeviceProviderCatalogEntry>(StringComparer.Ordinal);
        foreach (var entry in _entries)
        {
            byKind.TryAdd(entry.Kind, entry);
        }

        var providers = new List<DeviceProviderInfo>();
        var guests = new List<DeviceGuestInfo>();
        foreach (var kind in enabled.Distinct(StringComparer.Ordinal))
        {
            if (!byKind.TryGetValue(kind, out var entry))
            {
                continue;
            }

            await RefreshAsync(entry, cancellationToken).ConfigureAwait(false);
            var capabilities = entry.Capabilities;
            var probe = Probe(entry);
            var running = entry.RunningGuests();
            providers.Add(new DeviceProviderInfo(
                entry.Kind,
                capabilities.GuestOs,
                probe.Usable,
                probe.Usable ? null : probe.Reason,
                capabilities.Isolation.ToString(),
                capabilities.Headless,
                capabilities.Save,
                capabilities.Checkpoint,
                capabilities.Restore,
                capabilities.EgressEnforced,
                capabilities.MaxConcurrentInstances > 0 ? capabilities.MaxConcurrentInstances : null,
                running.Count,
                RestartRequired(entry)));
            guests.AddRange(running.Select(g => new DeviceGuestInfo(g.VmId, entry.Kind, capabilities.GuestOs, g.State)));
        }

        return WireReply.Ok(context.Connection.Codec, new DeviceListReply(providers, guests));
    }

    // Null (not an empty list) when nothing needs a restart, so the common reply is unchanged on the wire.
    private static IReadOnlyList<string>? RestartRequired(IDeviceProviderCatalogEntry entry)
    {
        var keys = entry.RestartRequiredKeys;
        return keys.Count == 0 ? null : [.. keys];
    }

    // A settings read that fails must not fail the whole list; the entry then reports its last loaded state.
    private static async ValueTask RefreshAsync(IDeviceProviderCatalogEntry entry, CancellationToken ct)
    {
        try
        {
            await entry.RefreshAsync(ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
        }
    }

    // A probe that throws must not fail the whole list. The reason is fixed text: an exception message may carry a host path.
    private static DeviceProbeResult Probe(IDeviceProviderCatalogEntry entry)
    {
        try
        {
            return entry.ProbeDevice();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return new DeviceProbeResult(false, "The host probe failed.");
        }
    }
}
