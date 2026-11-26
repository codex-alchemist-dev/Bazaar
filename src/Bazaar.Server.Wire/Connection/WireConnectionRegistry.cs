using System.Collections.Concurrent;

namespace Bazaar.Server.Wire.Connection;

/// <summary>The live sockets. Revocation and the per-device socket limit go through here.</summary>
public sealed class WireConnectionRegistry
{
    private readonly ConcurrentDictionary<Guid, (long Seq, IWireConnection Connection)> _connections = new();
    private long _seq;

    /// <summary>Live connections, oldest not guaranteed first.</summary>
    public IReadOnlyCollection<IWireConnection> Connections => _connections.Values.Select(v => v.Connection).ToArray();

    /// <summary>Connections of one device.</summary>
    public IReadOnlyList<IWireConnection> ForDevice(string deviceId) =>
        _connections.Values.Where(v => v.Connection.DeviceId == deviceId).OrderBy(v => v.Seq).Select(v => v.Connection).ToArray();

    /// <summary>Adds a connection and returns the older sockets of its device that exceed <paramref name="maxPerDevice"/>; the newest wins.</summary>
    internal IReadOnlyList<IWireConnection> Add(IWireConnection connection, int maxPerDevice)
    {
        _connections[connection.ConnectionId] = (Interlocked.Increment(ref _seq), connection);
        var mine = ForDevice(connection.DeviceId);
        return mine.Count <= maxPerDevice ? [] : mine.Take(mine.Count - maxPerDevice).ToArray();
    }

    internal void Remove(IWireConnection connection) => _connections.TryRemove(connection.ConnectionId, out _);

    /// <summary>Closes every socket of a device with the given code (4403 for a revoked device).</summary>
    public async ValueTask CloseDeviceAsync(string deviceId, int closeCode, string reason)
    {
        foreach (var c in ForDevice(deviceId))
        {
            await c.CloseAsync(closeCode, reason).ConfigureAwait(false);
        }
    }

    /// <summary>Closes every socket of a principal with the given code.</summary>
    public async ValueTask ClosePrincipalAsync(Guid principalId, int closeCode, string reason)
    {
        foreach (var c in _connections.Values.Select(v => v.Connection).Where(c => c.Principal.Id == principalId).ToArray())
        {
            await c.CloseAsync(closeCode, reason).ConfigureAwait(false);
        }
    }
}
