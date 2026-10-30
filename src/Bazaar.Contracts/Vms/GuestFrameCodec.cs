using System.Buffers.Binary;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;

namespace Bazaar.Contracts.Vms;

/// <summary>
/// Length-prefixed, versioned guest frames. Layout, all integers big-endian:
/// <c>length</c> (u32, bytes after this field), <c>version</c> (u8), <c>tagLength</c> (u8), <c>tag</c> (ASCII
/// message type name, such as <c>exec</c>), then the JSON body that fills the rest of the frame.
/// </summary>
public static class GuestFrameCodec
{
    /// <summary>Protocol version this build writes and accepts.</summary>
    public const byte Version = 1;

    /// <summary>Largest frame the codec writes or reads: the value of <c>length</c>.</summary>
    public const int MaxFrameBytes = 1024 * 1024;

    private const int _lengthBytes = 4;
    private const int _headerBytes = 2; // version and tag length, inside the counted length

    /// <summary>Writes one frame and flushes the stream.</summary>
    /// <exception cref="GuestFrameException">The message is larger than <see cref="MaxFrameBytes"/>.</exception>
    public static async Task WriteAsync(Stream stream, GuestMessage message, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(stream);
        ArgumentNullException.ThrowIfNull(message);

        var (tag, body) = Encode(message);
        var tagBytes = Encoding.ASCII.GetBytes(tag);
        long counted = _headerBytes + tagBytes.Length + body.Length;
        if (counted > MaxFrameBytes)
        {
            throw new GuestFrameException($"Frame for '{tag}' is {counted} bytes, over the {MaxFrameBytes}-byte limit.");
        }

        var frame = new byte[_lengthBytes + counted];
        BinaryPrimitives.WriteUInt32BigEndian(frame, (uint)counted);
        frame[_lengthBytes] = Version;
        frame[_lengthBytes + 1] = (byte)tagBytes.Length;
        tagBytes.CopyTo(frame, _lengthBytes + _headerBytes);
        body.CopyTo(frame, _lengthBytes + _headerBytes + tagBytes.Length);

        await stream.WriteAsync(frame, ct);
        await stream.FlushAsync(ct);
    }

    /// <summary>Reads one frame.</summary>
    /// <returns>The message, or null when the stream ends cleanly at a frame boundary.</returns>
    /// <exception cref="GuestFrameException">
    /// The frame is oversized, truncated, has an unknown version or type, or its body does not match the type.
    /// The channel must be closed after this.
    /// </exception>
    public static async Task<GuestMessage?> ReadAsync(Stream stream, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(stream);

        var length = new byte[_lengthBytes];
        if (await stream.ReadAsync(length.AsMemory(0, 1), ct) == 0) return null;
        await ReadExactAsync(stream, length.AsMemory(1), "frame length", ct);

        uint counted = BinaryPrimitives.ReadUInt32BigEndian(length);
        if (counted > MaxFrameBytes)
        {
            throw new GuestFrameException($"Frame of {counted} bytes is over the {MaxFrameBytes}-byte limit.");
        }

        if (counted < _headerBytes)
        {
            throw new GuestFrameException($"Frame of {counted} bytes is too short to hold a header.");
        }

        var frame = new byte[counted];
        await ReadExactAsync(stream, frame, "frame body", ct);

        if (frame[0] != Version)
        {
            throw new GuestFrameException($"Unsupported guest protocol version {frame[0]}; this build speaks {Version}.");
        }

        int tagLength = frame[1];
        if (_headerBytes + tagLength > frame.Length)
        {
            throw new GuestFrameException("Frame type tag runs past the end of the frame.");
        }

        var tag = Encoding.ASCII.GetString(frame, _headerBytes, tagLength);
        var body = frame.AsSpan(_headerBytes + tagLength);
        return Decode(tag, body);
    }

    private static async Task ReadExactAsync(Stream stream, Memory<byte> buffer, string what, CancellationToken ct)
    {
        try
        {
            await stream.ReadExactlyAsync(buffer, ct);
        }
        catch (EndOfStreamException ex)
        {
            throw new GuestFrameException($"Stream ended inside a {what}; the frame is truncated.", ex);
        }
    }

    private static (string Tag, byte[] Body) Encode(GuestMessage message) => message switch
    {
        Hello m => ("hello", JsonSerializer.SerializeToUtf8Bytes(m, GuestJsonContext.Default.Hello)),
        Exec m => ("exec", JsonSerializer.SerializeToUtf8Bytes(m, GuestJsonContext.Default.Exec)),
        Output m => ("output", JsonSerializer.SerializeToUtf8Bytes(m, GuestJsonContext.Default.Output)),
        Cancel m => ("cancel", JsonSerializer.SerializeToUtf8Bytes(m, GuestJsonContext.Default.Cancel)),
        Exit m => ("exit", JsonSerializer.SerializeToUtf8Bytes(m, GuestJsonContext.Default.Exit)),
        PutFile m => ("putFile", JsonSerializer.SerializeToUtf8Bytes(m, GuestJsonContext.Default.PutFile)),
        GetFile m => ("getFile", JsonSerializer.SerializeToUtf8Bytes(m, GuestJsonContext.Default.GetFile)),
        Heartbeat m => ("heartbeat", JsonSerializer.SerializeToUtf8Bytes(m, GuestJsonContext.Default.Heartbeat)),
        PtyOpen m => ("ptyOpen", JsonSerializer.SerializeToUtf8Bytes(m, GuestJsonContext.Default.PtyOpen)),
        PtyData m => ("ptyData", JsonSerializer.SerializeToUtf8Bytes(m, GuestJsonContext.Default.PtyData)),
        PtyResize m => ("ptyResize", JsonSerializer.SerializeToUtf8Bytes(m, GuestJsonContext.Default.PtyResize)),
        PtyClose m => ("ptyClose", JsonSerializer.SerializeToUtf8Bytes(m, GuestJsonContext.Default.PtyClose)),
        _ => throw new GuestFrameException($"No guest tag for message type {message.GetType().Name}."),
    };

    private static GuestMessage Decode(string tag, ReadOnlySpan<byte> body) => tag switch
    {
        "hello" => Parse(body, GuestJsonContext.Default.Hello, tag),
        "exec" => Parse(body, GuestJsonContext.Default.Exec, tag),
        "output" => Parse(body, GuestJsonContext.Default.Output, tag),
        "cancel" => Parse(body, GuestJsonContext.Default.Cancel, tag),
        "exit" => Parse(body, GuestJsonContext.Default.Exit, tag),
        "putFile" => Parse(body, GuestJsonContext.Default.PutFile, tag),
        "getFile" => Parse(body, GuestJsonContext.Default.GetFile, tag),
        "heartbeat" => Parse(body, GuestJsonContext.Default.Heartbeat, tag),
        "ptyOpen" => Parse(body, GuestJsonContext.Default.PtyOpen, tag),
        "ptyData" => Parse(body, GuestJsonContext.Default.PtyData, tag),
        "ptyResize" => Parse(body, GuestJsonContext.Default.PtyResize, tag),
        "ptyClose" => Parse(body, GuestJsonContext.Default.PtyClose, tag),
        _ => throw new GuestFrameException($"Unknown guest message type '{tag}'. The channel must close."),
    };

    private static T Parse<T>(ReadOnlySpan<byte> body, JsonTypeInfo<T> info, string tag) where T : GuestMessage
    {
        try
        {
            return JsonSerializer.Deserialize(body, info)
                ?? throw new GuestFrameException($"Body of '{tag}' is null.");
        }
        catch (JsonException ex)
        {
            throw new GuestFrameException($"Body of '{tag}' does not match its message type.", ex);
        }
    }
}
