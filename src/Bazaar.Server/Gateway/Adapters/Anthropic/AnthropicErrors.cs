using System.Text.Json;

namespace Bazaar.Server.Gateway.Adapters.Anthropic;

/// <summary>Reads Anthropic error bodies and error wording. Shared by the HTTP and in-stream error paths.</summary>
internal static class AnthropicErrors
{
    /// <summary>
    /// True when the text says the account has run out of credit or hit its spend limit. Anthropic reports this as a 429,
    /// so a 429 with this wording is <c>spend_capped</c> (no retry time) rather than <c>rate_limited</c>.
    /// </summary>
    public static bool IsSpendCap(string text) =>
        text.Contains("credit balance", StringComparison.OrdinalIgnoreCase)
        || text.Contains("spend limit", StringComparison.OrdinalIgnoreCase)
        || text.Contains("spend_limit", StringComparison.OrdinalIgnoreCase);

    /// <summary>The <c>error.message</c> of an error body such as <c>{"type":"error","error":{...}}</c>, or null if absent.</summary>
    public static string? ReadMessage(string body)
    {
        try
        {
            using var document = JsonDocument.Parse(body);
            var root = document.RootElement;
            if (root.ValueKind == JsonValueKind.Object
                && root.TryGetProperty("error", out var error)
                && error.ValueKind == JsonValueKind.Object
                && error.TryGetProperty("message", out var message)
                && message.ValueKind == JsonValueKind.String)
            {
                return message.GetString();
            }
        }
        catch (JsonException)
        {
            // Not JSON: the caller falls back to a generic message.
        }

        return null;
    }
}
