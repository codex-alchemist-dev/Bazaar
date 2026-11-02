using System.Collections.Immutable;
using System.Text;
using System.Text.Json;

namespace Bazaar.Server.Security.Readers;

/// <summary>
/// The schema validator for reader hand-overs (doc 15, "Reader and actor sessions"). Pure and deterministic: no I/O, no
/// clock, no shared state beyond the registry it reads. Rejects unknown schemas, wrong types, undeclared fields, over-limit
/// strings and arrays, out-of-range integers, bad enum values and bad URLs. Payloads over 64 KB of UTF-8 or nested deeper
/// than 8 container levels are refused before any field is read.
/// </summary>
public sealed class HandoverValidator : IHandoverValidator
{
    /// <summary>Largest accepted payload, as UTF-8 JSON text.</summary>
    public const int MaxPayloadBytes = 64 * 1024;

    /// <summary>Deepest accepted container nesting. The root object is depth 1.</summary>
    public const int MaxDepth = 8;

    private readonly IHandoverSchemaRegistry _registry;

    /// <summary>Creates a validator over a schema registry.</summary>
    public HandoverValidator(IHandoverSchemaRegistry registry)
    {
        ArgumentNullException.ThrowIfNull(registry);
        _registry = registry;
    }

    /// <inheritdoc />
    public HandoverResult Validate(string schemaId, JsonElement payload)
    {
        ArgumentNullException.ThrowIfNull(schemaId);

        if (!_registry.TryGet(schemaId, out var schema))
        {
            return Reject(HandoverRejectCode.UnknownSchema, "No schema is registered under that id.");
        }

        if (payload.ValueKind != JsonValueKind.Object)
        {
            return Reject(HandoverRejectCode.WrongType, "The payload must be a JSON object.");
        }

        if (Encoding.UTF8.GetByteCount(payload.GetRawText()) > MaxPayloadBytes)
        {
            return Reject(HandoverRejectCode.TooLarge, "The payload is larger than 64 KB.");
        }

        if (ExceedsDepth(payload, depth: 1))
        {
            return Reject(HandoverRejectCode.TooDeep, "The payload nests deeper than 8 levels.");
        }

        var declared = schema.Fields.ToDictionary(field => field.Name, StringComparer.Ordinal);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var values = ImmutableDictionary.CreateBuilder<string, HandoverValue>(StringComparer.Ordinal);

        foreach (var property in payload.EnumerateObject())
        {
            if (!seen.Add(property.Name))
            {
                return Reject(HandoverRejectCode.DuplicateField, "A field appears more than once.");
            }

            // Undeclared names come from the reader, so the detail never echoes them.
            if (!declared.TryGetValue(property.Name, out var field))
            {
                return Reject(HandoverRejectCode.UnknownField, "The payload has a field the schema does not declare.");
            }

            var outcome = ConvertField(field, property.Value);
            if (outcome.Rejection is not null)
            {
                return outcome.Rejection;
            }

            values[field.Name] = outcome.Value!;
        }

        foreach (var field in schema.Fields)
        {
            if (field.Required && !values.ContainsKey(field.Name))
            {
                return Reject(HandoverRejectCode.MissingField, $"Required field '{field.Name}' is missing.");
            }
        }

        return new HandoverResult.Accepted(AcceptedHandover.Create(schema.Id, values.ToImmutable()));
    }

    private static FieldOutcome ConvertField(HandoverField field, JsonElement value)
    {
        switch (field)
        {
            case StringField text:
                {
                    if (value.ValueKind != JsonValueKind.String)
                    {
                        return WrongType(field);
                    }

                    var s = value.GetString()!;
                    return CodePoints(s) > text.MaxLength
                        ? Fail(HandoverRejectCode.TooLong, $"Field '{field.Name}' is longer than {text.MaxLength} characters.")
                        : Accept(new TextValue(s));
                }

            case EnumField choice:
                {
                    if (value.ValueKind != JsonValueKind.String)
                    {
                        return WrongType(field);
                    }

                    var s = value.GetString()!;
                    return choice.Values.Contains(s, StringComparer.Ordinal)
                        ? Accept(new TextValue(s))
                        : Fail(HandoverRejectCode.BadEnumValue, $"Field '{field.Name}' is not one of the allowed values.");
                }

            case StringListField list:
                {
                    if (value.ValueKind != JsonValueKind.Array)
                    {
                        return WrongType(field);
                    }

                    if (value.GetArrayLength() > list.MaxCount)
                    {
                        return Fail(HandoverRejectCode.TooManyItems, $"Field '{field.Name}' has more than {list.MaxCount} items.");
                    }

                    var items = ImmutableArray.CreateBuilder<string>(value.GetArrayLength());
                    foreach (var item in value.EnumerateArray())
                    {
                        if (item.ValueKind != JsonValueKind.String)
                        {
                            return WrongType(field);
                        }

                        var s = item.GetString()!;
                        if (CodePoints(s) > list.ItemMaxLength)
                        {
                            return Fail(HandoverRejectCode.TooLong, $"An item in '{field.Name}' is longer than {list.ItemMaxLength} characters.");
                        }

                        items.Add(s);
                    }

                    return Accept(new TextListValue(items.MoveToImmutable()));
                }

            case IntegerField number:
                {
                    if (value.ValueKind != JsonValueKind.Number)
                    {
                        return WrongType(field);
                    }

                    if (!value.TryGetInt64(out var n) || n < number.Min || n > number.Max)
                    {
                        return Fail(HandoverRejectCode.OutOfRange, $"Field '{field.Name}' is not a whole number in its allowed range.");
                    }

                    return Accept(new IntegerValue(n));
                }

            case BoolField:
                {
                    if (value.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
                    {
                        return WrongType(field);
                    }

                    return Accept(new BoolValue(value.ValueKind == JsonValueKind.True));
                }

            case UrlField url:
                {
                    if (value.ValueKind != JsonValueKind.String)
                    {
                        return WrongType(field);
                    }

                    var s = value.GetString()!;
                    if (CodePoints(s) > url.MaxLength)
                    {
                        return Fail(HandoverRejectCode.TooLong, $"Field '{field.Name}' is longer than {url.MaxLength} characters.");
                    }

                    var valid = Uri.TryCreate(s, UriKind.Absolute, out var uri)
                        && uri.Scheme is "http" or "https"
                        && string.IsNullOrEmpty(uri.UserInfo);
                    return valid
                        ? Accept(new TextValue(s))
                        : Fail(HandoverRejectCode.BadUrl, $"Field '{field.Name}' is not an absolute http or https URL.");
                }

            default:
                throw new ArgumentOutOfRangeException(nameof(field), field, "Unknown field kind.");
        }
    }

    /// <summary>True when containers nest deeper than <see cref="MaxDepth"/>. Stops descending at the first excess, so recursion stays shallow.</summary>
    private static bool ExceedsDepth(JsonElement element, int depth)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                if (depth > MaxDepth)
                {
                    return true;
                }

                foreach (var property in element.EnumerateObject())
                {
                    if (ExceedsDepth(property.Value, depth + 1))
                    {
                        return true;
                    }
                }

                return false;

            case JsonValueKind.Array:
                if (depth > MaxDepth)
                {
                    return true;
                }

                foreach (var item in element.EnumerateArray())
                {
                    if (ExceedsDepth(item, depth + 1))
                    {
                        return true;
                    }
                }

                return false;

            default:
                return false;
        }
    }

    /// <summary>Length in Unicode code points, so an emoji counts once.</summary>
    private static int CodePoints(string text) => text.EnumerateRunes().Count();

    private static FieldOutcome WrongType(HandoverField field) =>
        Fail(HandoverRejectCode.WrongType, $"Field '{field.Name}' has the wrong JSON type.");

    private static FieldOutcome Accept(HandoverValue value) => new(value, null);

    private static FieldOutcome Fail(HandoverRejectCode code, string detail) => new(null, Reject(code, detail));

    private static HandoverResult.Rejected Reject(HandoverRejectCode code, string detail) => new(code, detail);

    private sealed record FieldOutcome(HandoverValue? Value, HandoverResult.Rejected? Rejection);
}
