using System.Text;
using System.Text.Json;

namespace Bazaar.Client.Athanor.Bridge;

/// <summary>
/// Encodes host messages to JSON and decodes editor messages from JSON (doc 52 §4). The codec only builds JSON through
/// the source-generated serializer. It never builds a script string from user text, so a document's content is data
/// and never code.
/// </summary>
public sealed class BridgeCodec
{
    /// <summary>The payload limit when the caller does not set one (doc 52 §4).</summary>
    public const long DefaultMaxPayloadBytes = 8L * 1024 * 1024;

    private const int MaxKeyLength = 64;

    private readonly long _maxPayloadBytes;

    /// <summary>Creates a codec. <paramref name="maxPayloadBytes"/> bounds each message in UTF-8 bytes, both directions.</summary>
    public BridgeCodec(long maxPayloadBytes = DefaultMaxPayloadBytes)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxPayloadBytes);
        _maxPayloadBytes = maxPayloadBytes;
    }

    /// <summary>The configured payload limit in UTF-8 bytes.</summary>
    public long MaxPayloadBytes => _maxPayloadBytes;

    /// <summary>Encodes a host message as one JSON text. Throws <see cref="ArgumentException"/> when the message is invalid or too large.</summary>
    public string Encode(HostMessage message)
    {
        ArgumentNullException.ThrowIfNull(message);
        if (message.Seq < 1)
        {
            throw new ArgumentException("The host sequence number must be positive.", nameof(message));
        }

        string json = JsonSerializer.Serialize(message, BridgeJsonContext.Default.HostMessage);
        if (Encoding.UTF8.GetByteCount(json) > _maxPayloadBytes)
        {
            throw new ArgumentException("The encoded bridge message exceeds the payload limit.", nameof(message));
        }

        return json;
    }

    /// <summary>
    /// Decodes one editor message. Rejects a payload over the limit, malformed JSON, an unknown or missing
    /// <c>type</c>, and a message whose fields are missing or out of range. It does not check <c>seq</c> order; use the
    /// overload with a <see cref="BridgeSequenceGuard"/> for that.
    /// </summary>
    public bool TryDecode(string json, out EditorMessage? message, out string? error)
    {
        message = null;
        error = null;

        if (json is null)
        {
            error = "The payload is empty.";
            return false;
        }

        if (Encoding.UTF8.GetByteCount(json) > _maxPayloadBytes)
        {
            error = "The payload exceeds the size limit.";
            return false;
        }

        using JsonDocument document = TryParse(json, out error);
        if (document is null)
        {
            return false;
        }

        JsonElement root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object
            || !root.TryGetProperty("type", out JsonElement typeElement)
            || typeElement.ValueKind != JsonValueKind.String)
        {
            error = "The payload has no message type.";
            return false;
        }

        if (!IsEditorType(typeElement.GetString()))
        {
            error = "The message type is not known.";
            return false;
        }

        EditorMessage? decoded;
        try
        {
            decoded = JsonSerializer.Deserialize(json, BridgeJsonContext.Default.EditorMessage);
        }
        catch (JsonException)
        {
            error = "The message fields do not match its type.";
            return false;
        }

        if (decoded is null || !IsValid(decoded, out error))
        {
            error ??= "The message is empty.";
            return false;
        }

        message = decoded;
        return true;
    }

    /// <summary>
    /// Decodes one editor message and then checks its <c>seq</c> with <paramref name="guard"/>. A message the guard
    /// refuses is rejected and the guard does not move.
    /// </summary>
    public bool TryDecode(string json, BridgeSequenceGuard guard, out EditorMessage? message, out string? error)
    {
        ArgumentNullException.ThrowIfNull(guard);

        if (!TryDecode(json, out message, out error))
        {
            return false;
        }

        if (!guard.TryAccept(message!.Seq))
        {
            message = null;
            error = "The sequence number is not increasing.";
            return false;
        }

        return true;
    }

    private static JsonDocument? TryParse(string json, out string? error)
    {
        try
        {
            error = null;
            return JsonDocument.Parse(json);
        }
        catch (JsonException)
        {
            error = "The payload is malformed JSON.";
            return null;
        }
    }

    private static bool IsEditorType(string? type) => type is "ready" or "changed" or "saveRequested"
        or "keyPressed" or "selection" or "error";

    private static bool IsValid(EditorMessage message, out string? error)
    {
        error = null;
        if (message.Seq < 1)
        {
            error = "The sequence number must be positive.";
            return false;
        }

        switch (message)
        {
            case ChangedMessage changed:
                if (!HasDocument(changed.DocumentId) || changed.Content is null || changed.Version < 0)
                {
                    error = "The changed message has an invalid field.";
                    return false;
                }

                return true;

            case SaveRequestedMessage save:
                if (!HasDocument(save.DocumentId))
                {
                    error = "The save message has an invalid document id.";
                    return false;
                }

                return true;

            case KeyPressedMessage key:
                if (string.IsNullOrWhiteSpace(key.Key) || key.Key.Length > MaxKeyLength)
                {
                    error = "The key message has an invalid key.";
                    return false;
                }

                return true;

            case SelectionMessage selection:
                if (!HasDocument(selection.DocumentId)
                    || selection.StartLine < 1 || selection.StartColumn < 1
                    || selection.EndLine < 1 || selection.EndColumn < 1)
                {
                    error = "The selection message has an invalid field.";
                    return false;
                }

                return true;

            case ErrorMessage errorMessage:
                if (string.IsNullOrWhiteSpace(errorMessage.Code) || errorMessage.Message is null
                    || (errorMessage.DocumentId is not null && !HasDocument(errorMessage.DocumentId)))
                {
                    error = "The error message has an invalid field.";
                    return false;
                }

                return true;

            default:
                return true;
        }
    }

    private static bool HasDocument(string? documentId) => !string.IsNullOrWhiteSpace(documentId);
}
