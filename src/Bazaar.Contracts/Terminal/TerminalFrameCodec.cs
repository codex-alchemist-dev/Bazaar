using System.Buffers;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization.Metadata;
using Bazaar.Contracts.Wire;

namespace Bazaar.Contracts.Terminal;

/// <summary>Stable lowercase <c>type</c> discriminators for the terminal frames.</summary>
public static class TerminalFrameTypes
{
    /// <summary>Client asks to open or attach.</summary>
    public const string Open = "open";

    /// <summary>Server accepted the open.</summary>
    public const string Opened = "opened";

    /// <summary>Output bytes from the shell.</summary>
    public const string Output = "output";

    /// <summary>Input bytes from the writer.</summary>
    public const string Input = "input";

    /// <summary>Size change from the writer.</summary>
    public const string Resize = "resize";

    /// <summary>Control hand-over.</summary>
    public const string Control = "control";

    /// <summary>Replay request from a resubscribing client.</summary>
    public const string Replay = "replay";

    /// <summary>The shell ended.</summary>
    public const string Exit = "exit";

    /// <summary>Client detaches.</summary>
    public const string Close = "close";

    /// <summary>Server refused or failed an operation.</summary>
    public const string Error = "error";
}

/// <summary>Why a terminal frame could not be encoded or decoded.</summary>
public enum TerminalFrameFault
{
    /// <summary>No fault.</summary>
    None = 0,

    /// <summary>The text is not JSON, or the top-level value is not an object.</summary>
    Malformed,

    /// <summary>The <c>type</c> discriminator is missing or not a string.</summary>
    MissingType,

    /// <summary>The <c>type</c> discriminator is not one of <see cref="TerminalFrameTypes"/>.</summary>
    UnknownType,

    /// <summary>A required field is missing or null.</summary>
    MissingField,

    /// <summary>A field has the wrong JSON type or an invalid base64 value.</summary>
    InvalidField,

    /// <summary>The terminal id is not valid (see <see cref="WireStreams.IsValidTerminalId"/>).</summary>
    InvalidTerminalId,

    /// <summary>An output or input payload is larger than <see cref="TerminalFrameCodec.MaxPayloadBytes"/>.</summary>
    Oversize,
}

/// <summary>Outcome of <see cref="TerminalFrameCodec.Decode"/>. <see cref="Frame"/> is set only when it succeeded.</summary>
/// <param name="Frame">The decoded frame, or null.</param>
/// <param name="Fault">The fault, or <see cref="TerminalFrameFault.None"/>.</param>
/// <param name="Detail">Human-readable text that never contains payload data, or null.</param>
public readonly record struct TerminalDecodeResult(TerminalFrame? Frame, TerminalFrameFault Fault, string? Detail)
{
    /// <summary>True when a frame was decoded.</summary>
    public bool IsSuccess => Frame is not null;
}

/// <summary>Outcome of <see cref="TerminalFrameCodec.Encode"/>. <see cref="Bytes"/> is set only when it succeeded.</summary>
/// <param name="Bytes">The UTF-8 JSON text, or null.</param>
/// <param name="Fault">The fault, or <see cref="TerminalFrameFault.None"/>.</param>
/// <param name="Detail">Human-readable text that never contains payload data, or null.</param>
public readonly record struct TerminalEncodeResult(byte[]? Bytes, TerminalFrameFault Fault, string? Detail)
{
    /// <summary>True when the frame was encoded.</summary>
    public bool IsSuccess => Bytes is not null;
}

/// <summary>
/// Encodes and decodes terminal frames as JSON objects with a <c>type</c> discriminator, for example
/// <c>{"type":"output","terminalId":"t1","seq":1,"bytes":"aGk="}</c>. Bodies use <see cref="TerminalJsonContext"/>.
/// Malformed peer input never throws: it comes back as a failed result.
/// </summary>
public static class TerminalFrameCodec
{
    /// <summary>Largest <c>bytes</c> payload of an output or input frame, before base64 (16 KiB).</summary>
    public const int MaxPayloadBytes = 16 * 1024;

    // Base64 of MaxPayloadBytes is 4 * ceil(n / 3) characters; used to refuse oversize strings before decoding.
    private const int _maxBase64Chars = (MaxPayloadBytes + 2) / 3 * 4;

    /// <summary>Encodes one frame. Fails for an unknown frame type, an invalid terminal id, or an oversize payload.</summary>
    public static TerminalEncodeResult Encode(TerminalFrame frame)
    {
        ArgumentNullException.ThrowIfNull(frame);

        if (!WireStreams.IsValidTerminalId(frame.TerminalId))
        {
            return new TerminalEncodeResult(null, TerminalFrameFault.InvalidTerminalId, "the terminal id is not valid");
        }

        if (frame is TerminalOutput { Bytes.Length: > MaxPayloadBytes } or TerminalInput { Bytes.Length: > MaxPayloadBytes })
        {
            return new TerminalEncodeResult(null, TerminalFrameFault.Oversize, "the payload is larger than 16 KiB");
        }

        JsonObject body;
        switch (frame)
        {
            case TerminalOpen f:
                body = Body(TerminalFrameTypes.Open, TerminalJsonContext.Default.TerminalOpen, f);
                break;
            case TerminalOpened f:
                body = Body(TerminalFrameTypes.Opened, TerminalJsonContext.Default.TerminalOpened, f);
                break;
            case TerminalOutput f:
                body = Body(TerminalFrameTypes.Output, TerminalJsonContext.Default.TerminalOutput, f);
                break;
            case TerminalInput f:
                body = Body(TerminalFrameTypes.Input, TerminalJsonContext.Default.TerminalInput, f);
                break;
            case TerminalResize f:
                body = Body(TerminalFrameTypes.Resize, TerminalJsonContext.Default.TerminalResize, f);
                break;
            case TerminalControl f:
                body = Body(TerminalFrameTypes.Control, TerminalJsonContext.Default.TerminalControl, f);
                break;
            case TerminalReplay f:
                body = Body(TerminalFrameTypes.Replay, TerminalJsonContext.Default.TerminalReplay, f);
                break;
            case TerminalExit f:
                body = Body(TerminalFrameTypes.Exit, TerminalJsonContext.Default.TerminalExit, f);
                break;
            case TerminalClose f:
                body = Body(TerminalFrameTypes.Close, TerminalJsonContext.Default.TerminalClose, f);
                break;
            case TerminalError f:
                body = Body(TerminalFrameTypes.Error, TerminalJsonContext.Default.TerminalError, f);
                break;
            default:
                return new TerminalEncodeResult(null, TerminalFrameFault.UnknownType,
                    $"no terminal type for {frame.GetType().Name}");
        }

        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            body.WriteTo(writer);
        }

        return new TerminalEncodeResult(buffer.WrittenMemory.ToArray(), TerminalFrameFault.None, null);
    }

    /// <summary>Decodes one frame from JSON text. Never throws for malformed input.</summary>
    public static TerminalDecodeResult Decode(ReadOnlySpan<byte> json)
    {
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(json.ToArray());
        }
        catch (JsonException)
        {
            return Fail(TerminalFrameFault.Malformed, "the frame is not valid JSON");
        }

        using (document)
        {
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return Fail(TerminalFrameFault.Malformed, "the frame is not a JSON object");
            }

            if (!root.TryGetProperty("type", out var typeElement))
            {
                return Fail(TerminalFrameFault.MissingType, "the frame has no 'type'");
            }

            if (typeElement.ValueKind != JsonValueKind.String)
            {
                return Fail(TerminalFrameFault.MissingType, "the frame 'type' is not a string");
            }

            return typeElement.GetString() switch
            {
                TerminalFrameTypes.Open => Parse<TerminalOpen>(root, ["terminalId", "cols", "rows"], TerminalJsonContext.Default.TerminalOpen),
                TerminalFrameTypes.Opened => Parse<TerminalOpened>(root, ["terminalId", "writerHeld", "replayFrom"], TerminalJsonContext.Default.TerminalOpened),
                TerminalFrameTypes.Output => Parse<TerminalOutput>(root, ["terminalId", "seq", "bytes"], TerminalJsonContext.Default.TerminalOutput),
                TerminalFrameTypes.Input => Parse<TerminalInput>(root, ["terminalId", "bytes"], TerminalJsonContext.Default.TerminalInput),
                TerminalFrameTypes.Resize => Parse<TerminalResize>(root, ["terminalId", "cols", "rows"], TerminalJsonContext.Default.TerminalResize),
                TerminalFrameTypes.Control => Parse<TerminalControl>(root, ["terminalId", "action"], TerminalJsonContext.Default.TerminalControl),
                TerminalFrameTypes.Replay => Parse<TerminalReplay>(root, ["terminalId", "fromSeq"], TerminalJsonContext.Default.TerminalReplay),
                TerminalFrameTypes.Exit => Parse<TerminalExit>(root, ["terminalId", "code"], TerminalJsonContext.Default.TerminalExit),
                TerminalFrameTypes.Close => Parse<TerminalClose>(root, ["terminalId"], TerminalJsonContext.Default.TerminalClose),
                TerminalFrameTypes.Error => Parse<TerminalError>(root, ["terminalId", "code", "message"], TerminalJsonContext.Default.TerminalError),
                _ => Fail(TerminalFrameFault.UnknownType, "the frame 'type' is not a known terminal type"),
            };
        }
    }

    private static JsonObject Body<T>(string type, JsonTypeInfo<T> info, T frame)
    {
        var node = JsonSerializer.SerializeToNode(frame, info) as JsonObject
            ?? throw new InvalidOperationException("A terminal frame must serialise to a JSON object.");
        node.Insert(0, "type", JsonValue.Create(type));
        return node;
    }

    private static TerminalDecodeResult Parse<T>(JsonElement root, string[] required, JsonTypeInfo<T> info)
        where T : TerminalFrame
    {
        foreach (var name in required)
        {
            if (!root.TryGetProperty(name, out var value) || value.ValueKind == JsonValueKind.Null)
            {
                return Fail(TerminalFrameFault.MissingField, $"the frame has no '{name}'");
            }
        }

        if (root.TryGetProperty("bytes", out var bytes) && bytes.ValueKind == JsonValueKind.String
            && bytes.GetString() is { } text && text.Length > _maxBase64Chars)
        {
            return Fail(TerminalFrameFault.Oversize, "the payload is larger than 16 KiB");
        }

        T frame;
        try
        {
            frame = root.Deserialize(info)
                ?? throw new JsonException("null frame");
        }
        catch (JsonException)
        {
            return Fail(TerminalFrameFault.InvalidField, "a field has the wrong type or an invalid value");
        }
        catch (InvalidOperationException)
        {
            return Fail(TerminalFrameFault.InvalidField, "a field has the wrong type or an invalid value");
        }

        if (!WireStreams.IsValidTerminalId(frame.TerminalId))
        {
            return Fail(TerminalFrameFault.InvalidTerminalId, "the terminal id is not valid");
        }

        if (frame is TerminalOutput { Bytes.Length: > MaxPayloadBytes } or TerminalInput { Bytes.Length: > MaxPayloadBytes })
        {
            return Fail(TerminalFrameFault.Oversize, "the payload is larger than 16 KiB");
        }

        return new TerminalDecodeResult(frame, TerminalFrameFault.None, null);
    }

    private static TerminalDecodeResult Fail(TerminalFrameFault fault, string detail) => new(null, fault, detail);
}
