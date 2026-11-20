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

/// <summary>A list source that can tell the pane when a refresh is worth trying (for example the server connection came up).</summary>
public interface IRefreshableDeviceListSource : IDeviceListSource
{
    /// <summary>Raised when the pane should fetch the list again. May fire on any thread.</summary>
    event EventHandler? RefreshRequested;
}

/// <summary>
/// Reads the list with the owner-only <c>vms.devices.list</c> wire request. Given a <see cref="DeferredWireRequester"/>,
/// it asks the pane to refresh once the requester is set.
/// </summary>
public sealed class WireDeviceListSource : IRefreshableDeviceListSource
{
    private readonly IWireRequester _requests;

    public WireDeviceListSource(IWireRequester requests)
    {
        _requests = requests ?? throw new ArgumentNullException(nameof(requests));
        if (requests is DeferredWireRequester deferred)
        {
            deferred.Attached += (_, _) => RefreshRequested?.Invoke(this, EventArgs.Empty);
        }
    }

    /// <inheritdoc />
    public event EventHandler? RefreshRequested;

    /// <inheritdoc />
    public Task<DeviceListReply> ListAsync(CancellationToken cancellationToken) =>
        _requests.RequestAsync<DeviceListRequest, DeviceListReply>(WireTypes.DeviceList, new DeviceListRequest(), cancellationToken);
}
