using System.Text.Json;

namespace Bazaar.Server.MailInbound;

/// <summary>Why a raw <c>mail_triage</c> payload was refused. Closed set; never carries payload text.</summary>
public enum MailTriageRejection
{
    None,
    Empty,
    TooLarge,
    NotJson,
    NotAnObject,
    DuplicateField,
    UnknownField,
    MissingField,
    WrongType,
    WrongMessageId,
    UnknownVerdict,
    TooManyCodes,
    UnknownCode,
    RepeatedCode,
}

/// <summary>The result of validating one payload. <see cref="Handover"/> is set only when <see cref="Rejection"/> is <see cref="MailTriageRejection.None"/>.</summary>
public sealed record MailTriageValidation(MailTriageHandover? Handover, MailTriageRejection Rejection)
{
    public bool IsValid => Handover is not null;
}

/// <summary>
/// Validates raw reader output against the <c>mail_triage</c> schema (<c>Schemas/mail_triage.schema.json</c>) by hand on a
/// <see cref="JsonDocument"/>: no reflection and no serializer, so it is trim and AOT safe, and it can refuse what a serializer
/// would accept (unknown fields, duplicate fields, wrong types, a different message id). Reader output is untrusted.
/// </summary>
public static class MailTriageHandoverValidator
{
    /// <summary>Largest payload read, in UTF-16 code units. A valid payload is a few hundred.</summary>
    public const int MaxPayloadLength = 1024;

    private const string _messageId = "message_id";
    private const string _verdict = "verdict";
    private const string _reasonCodes = "reason_codes";

    private static readonly JsonDocumentOptions _options = new() { MaxDepth = 4, AllowTrailingCommas = false, CommentHandling = JsonCommentHandling.Disallow };

    /// <summary>Validates <paramref name="payload"/> for the message <paramref name="expectedMessageId"/>.</summary>
    public static MailTriageValidation Validate(string? payload, Guid expectedMessageId)
    {
        if (string.IsNullOrWhiteSpace(payload))
        {
            return Reject(MailTriageRejection.Empty);
        }

        if (payload.Length > MaxPayloadLength)
        {
            return Reject(MailTriageRejection.TooLarge);
        }

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(payload, _options);
        }
        catch (JsonException)
        {
            return Reject(MailTriageRejection.NotJson);
        }

        using (document)
        {
            return ValidateRoot(document.RootElement, expectedMessageId);
        }
    }

    /// <summary>The <c>mail_triage</c> JSON for a hand-over, in the shape this validator accepts.</summary>
    public static string ToPayload(MailTriageHandover handover)
    {
        ArgumentNullException.ThrowIfNull(handover);
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            writer.WriteString(_messageId, handover.MailMessageId);
            writer.WriteString(_verdict, VerdictName(handover.Verdict));
            writer.WriteStartArray(_reasonCodes);
            foreach (var code in handover.ReasonCodes)
            {
                writer.WriteStringValue(code);
            }

            writer.WriteEndArray();
            writer.WriteEndObject();
        }

        return System.Text.Encoding.UTF8.GetString(stream.ToArray());
    }

    private static MailTriageValidation ValidateRoot(JsonElement root, Guid expectedMessageId)
    {
        if (root.ValueKind != JsonValueKind.Object)
        {
            return Reject(MailTriageRejection.NotAnObject);
        }

        JsonElement? idElement = null, verdictElement = null, codesElement = null;
        foreach (var property in root.EnumerateObject())
        {
            switch (property.Name)
            {
                case _messageId when idElement is null:
                    idElement = property.Value;
                    break;
                case _verdict when verdictElement is null:
                    verdictElement = property.Value;
                    break;
                case _reasonCodes when codesElement is null:
                    codesElement = property.Value;
                    break;
                case _messageId or _verdict or _reasonCodes:
                    return Reject(MailTriageRejection.DuplicateField);
                default:
                    return Reject(MailTriageRejection.UnknownField);
            }
        }

        if (idElement is not { } id || verdictElement is not { } verdictValue || codesElement is not { } codes)
        {
            return Reject(MailTriageRejection.MissingField);
        }

        if (id.ValueKind != JsonValueKind.String || verdictValue.ValueKind != JsonValueKind.String || codes.ValueKind != JsonValueKind.Array)
        {
            return Reject(MailTriageRejection.WrongType);
        }

        if (!Guid.TryParseExact(id.GetString(), "D", out var messageId) || messageId != expectedMessageId)
        {
            return Reject(MailTriageRejection.WrongMessageId);
        }

        if (!TryParseVerdict(verdictValue.GetString(), out var verdict))
        {
            return Reject(MailTriageRejection.UnknownVerdict);
        }

        if (codes.GetArrayLength() > MailTriageReasonCodes.MaxCodes)
        {
            return Reject(MailTriageRejection.TooManyCodes);
        }

        var list = new List<string>(codes.GetArrayLength());
        foreach (var item in codes.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.String)
            {
                return Reject(MailTriageRejection.WrongType);
            }

            var code = item.GetString()!;
            if (!MailTriageReasonCodes.IsReaderCode(code))
            {
                return Reject(MailTriageRejection.UnknownCode);
            }

            if (list.Contains(code, StringComparer.Ordinal))
            {
                return Reject(MailTriageRejection.RepeatedCode);
            }

            list.Add(code);
        }

        return new MailTriageValidation(new MailTriageHandover(messageId, verdict, list), MailTriageRejection.None);
    }

    private static MailTriageValidation Reject(MailTriageRejection rejection) => new(null, rejection);

    private static bool TryParseVerdict(string? name, out MailTriageVerdict verdict)
    {
        (var known, verdict) = name switch
        {
            "routine" => (true, MailTriageVerdict.Routine),
            "needs_attention" => (true, MailTriageVerdict.NeedsAttention),
            "escalate" => (true, MailTriageVerdict.Escalate),
            _ => (false, default(MailTriageVerdict)),
        };
        return known;
    }

    private static string VerdictName(MailTriageVerdict verdict) => verdict switch
    {
        MailTriageVerdict.Routine => "routine",
        MailTriageVerdict.NeedsAttention => "needs_attention",
        MailTriageVerdict.Escalate => "escalate",
        _ => throw new ArgumentOutOfRangeException(nameof(verdict)),
    };
}
