using System.Collections.Immutable;
using System.Text;
using System.Text.Json;

namespace Bazaar.Server.Community;

/// <summary>Why a community hand-over payload was refused. Stable codes for audit and tests.</summary>
public enum CommunityHandoverRejectCode
{
    /// <summary>The payload or a field has the wrong JSON type.</summary>
    WrongType,

    /// <summary>A required field is absent.</summary>
    MissingField,

    /// <summary>The payload carries a field the schema does not declare.</summary>
    UnknownField,

    /// <summary>A field appears twice.</summary>
    DuplicateField,

    /// <summary>The payload is larger than 64 KB of UTF-8 JSON.</summary>
    TooLarge,

    /// <summary>A string is longer than its limit.</summary>
    TooLong,

    /// <summary>An array has more items than its limit.</summary>
    TooManyItems,

    /// <summary>An integer is out of range.</summary>
    OutOfRange,

    /// <summary>A value is not an allowed enum value, or a source ref is not a GUID.</summary>
    BadValue,

    /// <summary>A text field looks like a URL. The schema has no URL type.</summary>
    UrlLikeText,

    /// <summary>source_refs is empty. Lineage is required.</summary>
    MissingLineage,
}

/// <summary>The outcome of validating a community hand-over payload.</summary>
public abstract record CommunityHandoverResult
{
    /// <summary>The payload passed.</summary>
    /// <param name="Summary">Only the validated, typed fields.</param>
    public sealed record Accepted(CommunityThreadSummary Summary) : CommunityHandoverResult;

    /// <summary>The payload failed. Nothing from it is delivered. The detail never echoes payload text.</summary>
    /// <param name="Code">Stable reason code.</param>
    /// <param name="Detail">Detail for the audit log.</param>
    public sealed record Rejected(CommunityHandoverRejectCode Code, string Detail) : CommunityHandoverResult;
}

/// <summary>
/// A validated <c>community_thread_summary</c>. <see cref="Summary"/> and <see cref="TopicTags"/> are data to show to an
/// actor as quoted material; they are never instructions and never select a tool or action.
/// </summary>
public sealed record CommunityThreadSummary(
    string Platform,
    string ItemKind,
    string TargetRef,
    string Summary,
    ImmutableArray<string> TopicTags,
    string Tone,
    ImmutableArray<string> Flags,
    bool ReplyWanted,
    int Confidence,
    ImmutableArray<Guid> SourceRefs);

/// <summary>
/// Validates a <c>community_thread_summary</c> payload using System.Text.Json only. Pure and deterministic. Mirrors the
/// Server's hand-over validator rules (doc 15) so the host can register the same schema there and get the same verdicts.
/// </summary>
public static class CommunityHandoverValidator
{
    /// <summary>Largest accepted payload, as UTF-8 JSON text.</summary>
    public const int MaxPayloadBytes = 64 * 1024;

    /// <summary>Validates a payload against <see cref="CommunityHandoverSchemas.ThreadSummary"/>.</summary>
    public static CommunityHandoverResult Validate(JsonElement payload)
    {
        if (payload.ValueKind != JsonValueKind.Object)
        {
            return Reject(CommunityHandoverRejectCode.WrongType, "The payload must be a JSON object.");
        }

        if (Encoding.UTF8.GetByteCount(payload.GetRawText()) > MaxPayloadBytes)
        {
            return Reject(CommunityHandoverRejectCode.TooLarge, "The payload is larger than 64 KB.");
        }

        var schema = CommunityHandoverSchemas.ThreadSummary;
        var declared = schema.Fields.ToDictionary(f => f.Name, StringComparer.Ordinal);
        var values = new Dictionary<string, object>(StringComparer.Ordinal);

        foreach (var property in payload.EnumerateObject())
        {
            if (values.ContainsKey(property.Name))
            {
                return Reject(CommunityHandoverRejectCode.DuplicateField, "A field appears more than once.");
            }

            if (!declared.TryGetValue(property.Name, out var spec))
            {
                return Reject(CommunityHandoverRejectCode.UnknownField, "The payload has a field the schema does not declare.");
            }

            var (value, rejection) = Convert(spec, property.Value);
            if (rejection is not null)
            {
                return rejection;
            }

            values[spec.Name] = value!;
        }

        foreach (var field in schema.Fields)
        {
            if (field.Required && !values.ContainsKey(field.Name))
            {
                return Reject(CommunityHandoverRejectCode.MissingField, $"Required field '{field.Name}' is missing.");
            }
        }

        var refs = (ImmutableArray<Guid>)values["source_refs"];
        if (refs.IsEmpty)
        {
            return Reject(CommunityHandoverRejectCode.MissingLineage, "source_refs needs at least one item.");
        }

        return new CommunityHandoverResult.Accepted(new CommunityThreadSummary(
            (string)values["platform"],
            (string)values["item_kind"],
            (string)values["target_ref"],
            (string)values["summary"],
            values.TryGetValue("topic_tags", out var tags) ? (ImmutableArray<string>)tags : [],
            (string)values["tone"],
            values.TryGetValue("flags", out var flags) ? (ImmutableArray<string>)flags : [],
            (bool)values["reply_wanted"],
            (int)(long)values["confidence"],
            refs));
    }

    private static (object? Value, CommunityHandoverResult.Rejected? Rejection) Convert(CommunityFieldSpec spec, JsonElement value)
    {
        switch (spec.Kind)
        {
            case CommunityFieldKind.Text:
                return CheckText(spec, value);

            case CommunityFieldKind.Enum:
                {
                    if (value.ValueKind != JsonValueKind.String)
                    {
                        return WrongType(spec);
                    }

                    var s = value.GetString()!;
                    return spec.Values.Contains(s, StringComparer.Ordinal)
                        ? (s, null)
                        : (null, Reject(CommunityHandoverRejectCode.BadValue, $"Field '{spec.Name}' is not one of the allowed values."));
                }

            case CommunityFieldKind.TextList:
            case CommunityFieldKind.EnumList:
            case CommunityFieldKind.GuidList:
                return ConvertList(spec, value);

            case CommunityFieldKind.WholeNumber:
                {
                    if (value.ValueKind != JsonValueKind.Number)
                    {
                        return WrongType(spec);
                    }

                    return value.TryGetInt64(out var n) && n >= spec.Min && n <= spec.Max
                        ? (n, null)
                        : (null, Reject(CommunityHandoverRejectCode.OutOfRange, $"Field '{spec.Name}' is not a whole number in its allowed range."));
                }

            case CommunityFieldKind.Bool:
                return value.ValueKind is JsonValueKind.True or JsonValueKind.False
                    ? (value.ValueKind == JsonValueKind.True, null)
                    : WrongType(spec);

            default:
                throw new ArgumentOutOfRangeException(nameof(spec), spec, "Unknown field kind.");
        }
    }

    private static (object? Value, CommunityHandoverResult.Rejected? Rejection) ConvertList(CommunityFieldSpec spec, JsonElement value)
    {
        if (value.ValueKind != JsonValueKind.Array)
        {
            return WrongType(spec);
        }

        if (value.GetArrayLength() > spec.MaxCount)
        {
            return (null, Reject(CommunityHandoverRejectCode.TooManyItems, $"Field '{spec.Name}' has more than {spec.MaxCount} items."));
        }

        var texts = ImmutableArray.CreateBuilder<string>();
        var guids = ImmutableArray.CreateBuilder<Guid>();
        foreach (var item in value.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.String)
            {
                return WrongType(spec);
            }

            var s = item.GetString()!;
            switch (spec.Kind)
            {
                case CommunityFieldKind.GuidList:
                    if (!Guid.TryParseExact(s, "D", out var g))
                    {
                        return (null, Reject(CommunityHandoverRejectCode.BadValue, $"An item in '{spec.Name}' is not a GUID."));
                    }

                    guids.Add(g);
                    break;

                case CommunityFieldKind.EnumList:
                    if (!spec.Values.Contains(s, StringComparer.Ordinal))
                    {
                        return (null, Reject(CommunityHandoverRejectCode.BadValue, $"An item in '{spec.Name}' is not an allowed value."));
                    }

                    texts.Add(s);
                    break;

                default:
                    var (text, rejection) = CheckText(spec, item);
                    if (rejection is not null)
                    {
                        return (null, rejection);
                    }

                    texts.Add((string)text!);
                    break;
            }
        }

        return spec.Kind == CommunityFieldKind.GuidList ? (guids.ToImmutable(), null) : (texts.ToImmutable(), null);
    }

    private static (object? Value, CommunityHandoverResult.Rejected? Rejection) CheckText(CommunityFieldSpec spec, JsonElement value)
    {
        if (value.ValueKind != JsonValueKind.String)
        {
            return WrongType(spec);
        }

        var s = value.GetString()!;
        if (s.EnumerateRunes().Count() > spec.MaxLength)
        {
            return (null, Reject(CommunityHandoverRejectCode.TooLong, $"Field '{spec.Name}' is longer than {spec.MaxLength} characters."));
        }

        return LooksLikeUrl(s)
            ? (null, Reject(CommunityHandoverRejectCode.UrlLikeText, $"Field '{spec.Name}' contains a URL. The schema has no URL type."))
            : (s, null);
    }

    /// <summary>True for a scheme separator, a www host or a bare domain-with-path token. Conservative on purpose.</summary>
    private static bool LooksLikeUrl(string text)
    {
        if (text.Contains("://", StringComparison.Ordinal) || text.Contains("www.", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        foreach (var token in text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))
        {
            var t = token.Trim('(', ')', '<', '>', '"', '\'', ',', ';', '.');
            var slash = t.IndexOf('/', StringComparison.Ordinal);
            if (slash <= 0)
            {
                continue;
            }

            var host = t[..slash];
            if (host.Contains('.', StringComparison.Ordinal) && !host.StartsWith('.') && !host.EndsWith('.'))
            {
                return true;
            }
        }

        return false;
    }

    private static (object? Value, CommunityHandoverResult.Rejected? Rejection) WrongType(CommunityFieldSpec spec) =>
        (null, Reject(CommunityHandoverRejectCode.WrongType, $"Field '{spec.Name}' has the wrong JSON type."));

    private static CommunityHandoverResult.Rejected Reject(CommunityHandoverRejectCode code, string detail) => new(code, detail);
}
