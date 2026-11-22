using Bazaar.Contracts.Wire;

namespace Bazaar.Server.Wire.Sync;

/// <summary>
/// A connection's bounded outbound queue (doc 02, backpressure). The connection manager (W3) drains it onto the
/// socket. Live events never wait: a full queue means the stream lags. Replay waits for room.
/// </summary>
public interface IConnectionOutbound
{
    /// <summary>Queues a live event; false when the queue is full.</summary>
    bool TryEnqueue(in Envelope envelope);

    /// <summary>Queues a replay event, waiting for room.</summary>
    ValueTask EnqueueAsync(Envelope envelope, CancellationToken ct);

    /// <summary>Queues a control frame (<c>lagged</c>, <c>stream.revoked</c>, <c>resync</c>) past the bound.</summary>
    void EnqueueControl(Envelope envelope);
}
