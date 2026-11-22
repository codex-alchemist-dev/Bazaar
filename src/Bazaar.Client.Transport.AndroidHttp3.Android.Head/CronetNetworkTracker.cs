using Bazaar.Wire.Abstractions;

namespace Bazaar.Client.Transport.AndroidHttp3.Android.Head;

/// <summary>
/// Turns the raw <c>ConnectivityManager.NetworkCallback</c> signals (a network became available, a network was lost) into the
/// <see cref="WireNetworkEvent"/>s the connection layer reports (doc 90 section 9a): <c>Handover</c> when the active network
/// changes while the device stays online, <c>Suspended</c> when the last network is gone, <c>Resumed</c> when one returns.
/// The first network seen at start-up is not an event. Pure logic, no Android types, so it is tested off-device.
/// </summary>
internal sealed class CronetNetworkTracker
{
    private readonly Lock _gate = new();
    private readonly TimeProvider _time;
    private readonly List<long> _available = [];
    private bool _suspended;

    public CronetNetworkTracker(TimeProvider? time = null) => _time = time ?? TimeProvider.System;

    /// <summary>A network became available (<c>onAvailable</c>). Returns the event to report, or null.</summary>
    public WireNetworkEvent? OnAvailable(long networkId)
    {
        lock (_gate)
        {
            if (_available.Contains(networkId)) return null;
            long? previous = _available.Count > 0 ? _available[^1] : null;
            _available.Add(networkId);
            if (_suspended)
            {
                _suspended = false;
                return Event(WireNetworkEventKind.Resumed, $"network {networkId}");
            }

            return previous is { } from ? Event(WireNetworkEventKind.Handover, $"network {from} to {networkId}") : null;
        }
    }

    /// <summary>A network was lost (<c>onLost</c>). Returns the event to report, or null.</summary>
    public WireNetworkEvent? OnLost(long networkId)
    {
        lock (_gate)
        {
            var index = _available.IndexOf(networkId);
            if (index < 0) return null;
            var wasActive = index == _available.Count - 1;
            _available.RemoveAt(index);
            if (_available.Count == 0)
            {
                _suspended = true;
                return Event(WireNetworkEventKind.Suspended, $"network {networkId} lost");
            }

            return wasActive ? Event(WireNetworkEventKind.Handover, $"network {networkId} to {_available[^1]}") : null;
        }
    }

    private WireNetworkEvent Event(WireNetworkEventKind kind, string detail) => new(kind, _time.GetUtcNow(), detail);
}
