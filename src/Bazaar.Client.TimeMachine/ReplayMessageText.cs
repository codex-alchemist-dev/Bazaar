using System.Text.Json;
using Bazaar.Contracts.Gc;

namespace Bazaar.Client.TimeMachine;

/// <summary>
/// Decodes a replayed message's payload into the text the scrubber shows, by event kind (<see cref="GcEventKinds"/>), the way the
/// GC pane treats kinds: message rows (<c>message</c>, <c>message_edit</c>) show their text; every other registered kind is a
/// non-message event and shows a neutral placeholder; a kind this client does not know shows a placeholder naming it. Never throws.
/// </summary>
/// <remarks>
/// The replay wire carries no kind (the projector only returns <c>message</c> and <c>message_edit</c> rows), so
/// <see cref="WireReplayClient"/> passes a null kind, which is read as a message. The other branches keep the decoder correct if
/// the frame ever carries other kinds.
/// </remarks>
public static class ReplayMessageText
{
    /// <summary>Placeholder for a message whose payload is valid JSON but has no text.</summary>
    public const string NoText = "(no text)";

    /// <summary>Decodes <paramref name="payloadJson"/> for <paramref name="kind"/>. Returns null when a message has no payload.</summary>
    /// <param name="kind">The event kind, or null for a message row.</param>
    /// <param name="payloadJson">The kind-specific payload, or null.</param>
    public static string? Decode(string? kind, string? payloadJson)
    {
        switch (kind)
        {
            case null:
            case GcEventKinds.Message:
            case GcEventKinds.MessageEdit:
                return DecodeMessage(payloadJson);
            case GcEventKinds.System:
            case GcEventKinds.Structured:
            case GcEventKinds.PrEvent:
            case GcEventKinds.Quarantine:
            case GcEventKinds.Handover:
            case GcEventKinds.ExternalMail:
                return $"[{kind.Replace('_', ' ')} event]";
            default:
                // Kinds are registry-backed strings, so a newer server may send one this client has never seen.
                return $"[unsupported message kind: {Printable(kind)}]";
        }
    }

    private static string? DecodeMessage(string? payloadJson)
    {
        if (string.IsNullOrEmpty(payloadJson))
        {
            return null;
        }

        try
        {
            using var doc = JsonDocument.Parse(payloadJson);
            var root = doc.RootElement;
            if (root.ValueKind == JsonValueKind.String)
            {
                return root.GetString();
            }

            if (root.ValueKind == JsonValueKind.Object
                && root.TryGetProperty("text", out var text)
                && text.ValueKind == JsonValueKind.String)
            {
                return text.GetString();
            }

            // A body shape this client has no renderer for: name its kind when it gives one, never dump the JSON into the timeline.
            if (root.ValueKind == JsonValueKind.Object
                && root.TryGetProperty("kind", out var bodyKind)
                && bodyKind.ValueKind == JsonValueKind.String
                && !string.IsNullOrWhiteSpace(bodyKind.GetString()))
            {
                return $"[{Printable(bodyKind.GetString()!)} message]";
            }

            return NoText;
        }
        catch (JsonException)
        {
            // Not JSON: show it as it came.
            return payloadJson;
        }
    }

    private static string Printable(string kind) =>
        kind.Length <= 40 ? kind : string.Concat(kind.AsSpan(0, 40), "...");
}
