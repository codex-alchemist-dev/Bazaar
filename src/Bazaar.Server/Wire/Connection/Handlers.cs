using Bazaar.Contracts.Wire;
using Bazaar.Contracts.Wire.Codec;

namespace Bazaar.Server.Wire.Connection;

/// <summary>What a request handler answers: one payload, or one error.</summary>
public sealed record WireReply
{
    private WireReply()
    {
    }

    /// <summary>True for a <c>res</c>; false for an <c>err</c>.</summary>
    public bool IsOk { get; private init; }

    /// <summary>Encoded payload of a <c>res</c>.</summary>
    public ReadOnlyMemory<byte> Payload { get; private init; }

    /// <summary>The error of an <c>err</c>.</summary>
    public WireError? Error { get; private init; }

    /// <summary>A successful reply carrying an already encoded payload.</summary>
    public static WireReply Ok(ReadOnlyMemory<byte> payload = default) => new() { IsOk = true, Payload = payload };

    /// <summary>A successful reply; the payload is encoded with <paramref name="codec"/>.</summary>
    public static WireReply Ok<T>(IWireCodec codec, T payload) => Ok(codec.EncodePayload(payload));

    /// <summary>A failed reply. <paramref name="code"/> is one of <see cref="WireErrorCodes"/>.</summary>
    public static WireReply Fail(string code, string? message = null, int? retryAfterMs = null) =>
        new() { Error = new WireError(code, message, retryAfterMs) };

    /// <summary>True when the same request id may be retried and must run again (a server fault).</summary>
    internal bool IsRetryable => Error?.Code == WireErrorCodes.Internal;
}

/// <summary>One request, as a handler sees it.</summary>
/// <param name="Connection">The connection it arrived on.</param>
/// <param name="Request">The decoded envelope.</param>
public sealed record WireRequestContext(IWireConnection Connection, Envelope Request)
{
    /// <summary>Decodes the request payload; a payload that does not fit becomes a <see cref="WireFormatException"/>.</summary>
    public T Payload<T>() => Connection.Codec.DecodePayload<T>(Request);
}

/// <summary>Handles one request type. Register one per type; W4 and later modules add theirs.</summary>
public interface IWireRequestHandler
{
    /// <summary>The message type handled, such as <c>subscribe</c>.</summary>
    string Type { get; }

    /// <summary>Answers a request. Throwing is answered with <c>err internal</c>.</summary>
    ValueTask<WireReply> HandleAsync(WireRequestContext context, CancellationToken cancellationToken);
}

/// <summary>A live, authenticated socket as other modules see it.</summary>
public interface IWireConnection
{
    /// <summary>Unique id of this connection.</summary>
    Guid ConnectionId { get; }

    /// <summary>Authenticated principal.</summary>
    PrincipalRef Principal { get; }

    /// <summary>Device id.</summary>
    string DeviceId { get; }

    /// <summary>Device class from <c>hello</c>.</summary>
    string DeviceClass { get; }

    /// <summary>The negotiated codec.</summary>
    IWireCodec Codec { get; }

    /// <summary>Cursors the client sent in <c>hello</c>, or empty.</summary>
    IReadOnlyList<StreamCursor> HelloCursors { get; }

    /// <summary>Cancelled when the connection ends.</summary>
    CancellationToken Closed { get; }

    /// <summary>
    /// Queues a server event. Returns false when the queue holds <c>wire.outbound_queue_max</c> events or the
    /// connection is closed; the caller then stops live delivery and sends <c>lagged</c> through
    /// <see cref="TryEnqueueLagged"/>.
    /// </summary>
    bool TryEnqueueEvent(Envelope envelope);

    /// <summary>Queues a <c>lagged</c> notice even when the event queue is full. Use once per overflow.</summary>
    bool TryEnqueueLagged(Lagged lagged);

    /// <summary>Closes the socket with a wire close code.</summary>
    ValueTask CloseAsync(int closeCode, string reason);
}
