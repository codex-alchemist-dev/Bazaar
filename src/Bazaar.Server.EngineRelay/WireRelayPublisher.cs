using Bazaar.Contracts.Engines;
using Bazaar.Contracts.Wire;
using Bazaar.Server.EngineDispatch;
using Bazaar.Server.Wire.Connection;
using Bazaar.Server.Wire.Sync;

namespace Bazaar.Server.EngineRelay;

/// <summary>
/// <see cref="IRelayPublisher"/> over the wire kernel: sends an <c>evt</c> of type <see cref="EngineRelayWire.EventType"/> on stream
/// <see cref="EngineRelayWire.Stream"/> to one device. Re-checks the device policy at send time, so a connection that stopped being
/// the target is never written to. The event is not replayed after a reconnect. When the connection ends, <c>onDeviceClosed</c> runs
/// (the composition root points it at <c>EngineRelayBroker.DeviceClosed</c>), failing every relay pending on it.
/// </summary>
public sealed class WireRelayPublisher(
    IWireConnectionDirectory connections,
    Action<string> onDeviceClosed,
    SyncOptions? options = null,
    TimeProvider? time = null) : IRelayPublisher
{
    private readonly int _protocolMajor = (options ?? new SyncOptions()).ProtocolMajor;
    private readonly TimeProvider _time = time ?? TimeProvider.System;

    /// <inheritdoc />
    public ValueTask PublishAsync(string deviceId, EngineRelayRequestEvent relayEvent, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrEmpty(deviceId);
        ArgumentNullException.ThrowIfNull(relayEvent);

        IWireConnection connection = connections.Connections
            .Where(c => string.Equals(c.DeviceId, deviceId, StringComparison.Ordinal) && RelayDevicePolicy.IsTarget(c))
            .FirstOrDefault()
            ?? throw new InvalidOperationException("The relay device is not connected.");

        var envelope = new Envelope(
            _protocolMajor,
            EngineRelayWire.EventType,
            EnvelopeKind.Event,
            null,
            null,
            EngineRelayWire.Stream,
            null,
            _time.GetUtcNow(),
            connection.Codec.EncodePayload(EngineRelayFrameMapping.ToFrame(relayEvent)));

        if (!connection.TryEnqueueEvent(envelope))
        {
            throw new InvalidOperationException("The relay device's outbound queue is full or closed.");
        }

        // Registered after the event is queued, so a failed publish leaves nothing behind. A connection that already closed runs it now.
        connection.Closed.Register(static state =>
        {
            var (callback, id) = ((Action<string>, string))state!;
            callback(id);
        }, (onDeviceClosed, deviceId));
        return ValueTask.CompletedTask;
    }
}
