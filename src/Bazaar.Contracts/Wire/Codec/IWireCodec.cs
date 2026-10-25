using System.Buffers;

namespace Bazaar.Contracts.Wire.Codec;

/// <summary>
/// Turns envelopes and payload records into frames and back. The codec is chosen in <c>hello</c> and
/// <c>welcome</c>; JSON is the only one built (spike S6). The envelope's payload stays encoded until
/// <see cref="DecodePayload{T}"/>, so an unknown message type can be answered without parsing it.
/// </summary>
public interface IWireCodec
{
    /// <summary>Name negotiated in <c>hello</c>, such as <c>json</c>.</summary>
    string Name { get; }

    /// <summary>True when frames are sent as WebSocket binary messages; false for text.</summary>
    bool IsBinary { get; }

    /// <summary>Writes one frame.</summary>
    void Encode(in Envelope envelope, IBufferWriter<byte> writer);

    /// <summary>Reads one frame. Unknown envelope fields are ignored.</summary>
    /// <exception cref="WireFormatException">The frame is not a valid envelope.</exception>
    Envelope Decode(ReadOnlySpan<byte> frame);

    /// <summary>Encodes a payload record for <see cref="Envelope.P"/>.</summary>
    /// <exception cref="NotSupportedException"><typeparamref name="T"/> is not a registered wire payload.</exception>
    ReadOnlyMemory<byte> EncodePayload<T>(T payload);

    /// <summary>Decodes the envelope's payload. Unknown payload fields are ignored.</summary>
    /// <exception cref="WireFormatException">The payload is missing or does not match <typeparamref name="T"/>.</exception>
    /// <exception cref="NotSupportedException"><typeparamref name="T"/> is not a registered wire payload.</exception>
    T DecodePayload<T>(in Envelope envelope);
}
