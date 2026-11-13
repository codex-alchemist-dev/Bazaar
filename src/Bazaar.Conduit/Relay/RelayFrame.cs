using System.Text.Json;
using System.Text.Json.Serialization;

namespace Bazaar.Conduit.Relay;

/// <summary>
/// A control message exchanged with the relay. The relay forwards client bytes separately; frames carry only control
/// traffic such as registration. A frame never holds a secret: the room is an opaque server id.
/// </summary>
/// <param name="Type">Frame kind, for example <c>register</c>.</param>
/// <param name="Room">Opaque room id, when the frame names one.</param>
public sealed record RelayFrame(string Type, string? Room = null)
{
    /// <summary>Frame type that registers this server's room with the relay.</summary>
    public const string RegisterType = "register";

    /// <summary>Builds the registration frame for <paramref name="room"/>.</summary>
    public static RelayFrame Register(string room) => new(RegisterType, room);
}

/// <summary>Source-generated JSON metadata for relay frames.</summary>
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(RelayFrame))]
internal sealed partial class RelayJsonContext : JsonSerializerContext
{
}

/// <summary>Encodes and decodes relay frames as JSON.</summary>
public static class RelayFrameCodec
{
    /// <summary>Serializes <paramref name="frame"/> to UTF-8 JSON.</summary>
    public static byte[] Encode(RelayFrame frame)
    {
        ArgumentNullException.ThrowIfNull(frame);
        return JsonSerializer.SerializeToUtf8Bytes(frame, RelayJsonContext.Default.RelayFrame);
    }

    /// <summary>Parses a frame, or returns null when the bytes are not a frame with a type.</summary>
    public static RelayFrame? Decode(ReadOnlySpan<byte> utf8Json)
    {
        try
        {
            var frame = JsonSerializer.Deserialize(utf8Json, RelayJsonContext.Default.RelayFrame);
            return frame?.Type is { Length: > 0 } ? frame : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
