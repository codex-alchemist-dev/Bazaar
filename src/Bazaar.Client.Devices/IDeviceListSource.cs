using Bazaar.Client.Abstractions.Wire;
using Bazaar.Contracts.Vms;
using Bazaar.Contracts.Wire;

namespace Bazaar.Client.Devices;

/// <summary>Where the Devices pane reads the enabled providers and running guests from (doc 62 section 9.1).</summary>
public interface IDeviceListSource
{
    /// <summary>Asks the server for the current list.</summary>
    Task<DeviceListReply> ListAsync(CancellationToken cancellationToken);
}

/// <summary>Reads the list with the owner-only <c>vms.devices.list</c> wire request.</summary>
public sealed class WireDeviceListSource(IWireRequester requests) : IDeviceListSource
{
    /// <inheritdoc />
    public Task<DeviceListReply> ListAsync(CancellationToken cancellationToken) =>
        requests.RequestAsync<DeviceListRequest, DeviceListReply>(WireTypes.DeviceList, new DeviceListRequest(), cancellationToken);
}
