using System.Text.Json;
using System.Text.Json.Serialization;

namespace Bazaar.Conduit.PeerToPeer;

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, WriteIndented = false)]
[JsonSerializable(typeof(SignalMessage))]
internal sealed partial class SignalingJsonContext : JsonSerializerContext
{
}

/// <summary>JSON codec for <see cref="SignalMessage"/>, source generated with System.Text.Json.</summary>
public static class SignalingCodec
{
    /// <summary>Serializes a message to its wire text.</summary>
    public static string Encode(SignalMessage message)
    {
        ArgumentNullException.ThrowIfNull(message);

        return JsonSerializer.Serialize(message, SignalingJsonContext.Default.SignalMessage);
    }

    /// <summary>
    /// Parses wire text. Returns false for malformed JSON, a missing field, or an unknown kind. Never throws on bad input.
    /// </summary>
    public static bool TryDecode(string text, out SignalMessage? message)
    {
        message = null;
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        try
        {
            var decoded = JsonSerializer.Deserialize(text, SignalingJsonContext.Default.SignalMessage);
            if (decoded is null
                || decoded.Kind is null
                || decoded.SessionId is null
                || decoded.Payload is null
                || !SignalKinds.IsKnown(decoded.Kind))
            {
                return false;
            }

            message = decoded;
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }
}
