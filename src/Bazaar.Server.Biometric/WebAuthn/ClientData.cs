using System.Buffers.Text;
using System.Text.Json;

namespace Bazaar.Server.Biometric.WebAuthn;

/// <summary>The signed client data of a Bazaar biometric assertion (doc 48 section 8.1). Parsed strictly: a missing or mistyped member refuses.</summary>
internal sealed record ClientData(string Type, byte[] Challenge, string Origin, string FactorName, string LivenessName, long AtMilliseconds)
{
    public const int MaxLength = 2048;

    public static bool TryParse(ReadOnlySpan<byte> json, out ClientData? parsed)
    {
        parsed = null;
        if (json.IsEmpty || json.Length > MaxLength)
            return false;

        try
        {
            var reader = new Utf8JsonReader(json, new JsonReaderOptions { MaxDepth = 4, CommentHandling = JsonCommentHandling.Disallow });
            using var doc = JsonDocument.ParseValue(ref reader);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object || !TryString(root, "type", out var type) || !TryString(root, "challenge", out var challengeText)
                || !TryString(root, "origin", out var origin))
                return false;

            // A cross-origin request is never a device assertion.
            if (root.TryGetProperty("crossOrigin", out var cross) && cross.ValueKind != JsonValueKind.False)
                return false;

            if (!root.TryGetProperty("bazaar", out var bazaar) || bazaar.ValueKind != JsonValueKind.Object
                || !TryString(bazaar, "factor", out var factor) || !TryString(bazaar, "liveness", out var liveness)
                || !bazaar.TryGetProperty("at", out var at) || at.ValueKind != JsonValueKind.Number || !at.TryGetInt64(out var atMs))
                return false;

            var challenge = new byte[32];
            if (challengeText.Length != 43 || !Base64Url.TryDecodeFromChars(challengeText, challenge, out var written) || written != 32)
                return false;

            parsed = new ClientData(type, challenge, origin, factor, liveness, atMs);
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static bool TryString(JsonElement element, string name, out string value)
    {
        value = string.Empty;
        if (!element.TryGetProperty(name, out var p) || p.ValueKind != JsonValueKind.String)
            return false;
        value = p.GetString() ?? string.Empty;
        return value.Length > 0;
    }
}
