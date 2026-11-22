using System.Text.Json;

namespace Bazaar.Client.Studio.Abstractions;

/// <summary>Why a relay call was refused or failed. The server maps these to capability <c>unavailable</c> reasons (doc 20).</summary>
public enum StudioRelayErrorCode
{
    /// <summary>Studio is not open, the bridge is offline, or the transport failed.</summary>
    Unavailable,

    /// <summary>The <c>studio_id</c> is not one of the open Studios.</summary>
    StudioUnknown,

    /// <summary>No <c>studio_id</c> was given; there is no default.</summary>
    StudioIdRequired,

    /// <summary>The grant does not cover the tool, or could not be verified (fail closed).</summary>
    GrantDenied,

    /// <summary>The tool is not in the allow-list, or its arguments are not acceptable.</summary>
    ToolNotAllowed,

    /// <summary>Studio did not answer within the per-call timeout.</summary>
    Timeout,
}

/// <summary>A relay error: a code plus human-readable reason text.</summary>
/// <param name="Code">The error code.</param>
/// <param name="Reason">Why, for the capability's <c>unavailable</c> reason and for logs. Never contains Studio content.</param>
public sealed record StudioRelayError(StudioRelayErrorCode Code, string Reason)
{
    /// <summary>Builds the failure payload <c>{"error":{"code":"...","reason":"..."}}</c>.</summary>
    public JsonElement ToPayload()
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            writer.WriteStartObject("error");
            writer.WriteString("code", Code.ToString());
            writer.WriteString("reason", Reason);
            writer.WriteEndObject();
            writer.WriteEndObject();
        }

        using var doc = JsonDocument.Parse(stream.ToArray());
        return doc.RootElement.Clone();
    }

    /// <summary>Reads an error back from a failure payload; false when the payload is not a relay error.</summary>
    public static bool TryFromPayload(JsonElement payload, out StudioRelayError? error)
    {
        error = null;
        if (payload.ValueKind != JsonValueKind.Object
            || !payload.TryGetProperty("error", out var body)
            || body.ValueKind != JsonValueKind.Object
            || !body.TryGetProperty("code", out var code)
            || code.ValueKind != JsonValueKind.String
            || !Enum.TryParse<StudioRelayErrorCode>(code.GetString(), out var parsed))
        {
            return false;
        }

        var reason = body.TryGetProperty("reason", out var r) && r.ValueKind == JsonValueKind.String ? r.GetString() ?? "" : "";
        error = new StudioRelayError(parsed, reason);
        return true;
    }
}
