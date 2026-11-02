using System.Collections.Immutable;
using System.Text.RegularExpressions;

namespace Bazaar.Server.Security.Readers;

/// <summary>
/// A typed field in a hand-over schema. Each kind carries its own limits; the validator checks a payload against them.
/// URL fields exist only when a schema declares them (doc 15: no URL type unless declared).
/// </summary>
public abstract record HandoverField(string Name, bool Required);

/// <summary>A string of at most <paramref name="MaxLength"/> code points.</summary>
public sealed record StringField(string Name, int MaxLength, bool Required = true) : HandoverField(Name, Required);

/// <summary>A string that must be one of the listed values (case-sensitive).</summary>
public sealed record EnumField(string Name, ImmutableArray<string> Values, bool Required = true) : HandoverField(Name, Required);

/// <summary>An array of at most <paramref name="MaxCount"/> strings, each at most <paramref name="ItemMaxLength"/> code points.</summary>
public sealed record StringListField(string Name, int MaxCount, int ItemMaxLength, bool Required = true) : HandoverField(Name, Required);

/// <summary>A whole number from <paramref name="Min"/> to <paramref name="Max"/>, inclusive.</summary>
public sealed record IntegerField(string Name, long Min, long Max, bool Required = true) : HandoverField(Name, Required);

/// <summary>A JSON boolean.</summary>
public sealed record BoolField(string Name, bool Required = true) : HandoverField(Name, Required);

/// <summary>An absolute http or https URL of at most <paramref name="MaxLength"/> code points. Only when declared.</summary>
public sealed record UrlField(string Name, int MaxLength, bool Required = true) : HandoverField(Name, Required);

/// <summary>
/// An immutable hand-over schema: the id a reader names in <c>submit_handover</c> and the fields a payload may carry.
/// Built only through <see cref="Create"/>, which rejects malformed definitions at registration time.
/// </summary>
public sealed partial class HandoverSchema
{
    private HandoverSchema(string id, ImmutableArray<HandoverField> fields)
    {
        Id = id;
        Fields = fields;
    }

    /// <summary>Schema id, for example <c>mail_triage</c>.</summary>
    public string Id { get; }

    /// <summary>The declared fields, in declaration order. Names are unique.</summary>
    public ImmutableArray<HandoverField> Fields { get; }

    /// <summary>Validates a definition and returns the schema.</summary>
    /// <exception cref="ArgumentException">The id or a field is malformed, or a name repeats.</exception>
    public static HandoverSchema Create(string id, params HandoverField[] fields)
    {
        ArgumentNullException.ThrowIfNull(id);
        ArgumentNullException.ThrowIfNull(fields);

        if (!SchemaIdPattern().IsMatch(id))
        {
            throw new ArgumentException("Schema id must be lowercase letters, digits, underscores or dots, starting with a letter.", nameof(id));
        }

        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var field in fields)
        {
            ArgumentNullException.ThrowIfNull(field);
            ValidateField(field);
            if (!names.Add(field.Name))
            {
                throw new ArgumentException($"Field '{field.Name}' is declared twice in schema '{id}'.", nameof(fields));
            }
        }

        return new HandoverSchema(id, [.. fields]);
    }

    private static void ValidateField(HandoverField field)
    {
        if (!FieldNamePattern().IsMatch(field.Name))
        {
            throw new ArgumentException($"Field name '{field.Name}' must be lowercase letters, digits or underscores.", nameof(field));
        }

        switch (field)
        {
            case StringField s when s.MaxLength < 1:
            case UrlField u when u.MaxLength < 1:
            case StringListField l when l.MaxCount < 1 || l.ItemMaxLength < 1:
                throw new ArgumentException($"Field '{field.Name}' needs positive length or count limits.", nameof(field));

            case IntegerField i when i.Min > i.Max:
                throw new ArgumentException($"Field '{field.Name}' has a minimum above its maximum.", nameof(field));

            case EnumField e when e.Values.IsDefaultOrEmpty || e.Values.Any(string.IsNullOrEmpty) || e.Values.Distinct(StringComparer.Ordinal).Count() != e.Values.Length:
                throw new ArgumentException($"Field '{field.Name}' needs a non-empty list of distinct, non-empty values.", nameof(field));
        }
    }

    [GeneratedRegex("^[a-z][a-z0-9_.]{0,63}$", RegexOptions.CultureInvariant)]
    private static partial Regex SchemaIdPattern();

    [GeneratedRegex("^[a-z][a-z0-9_]{0,63}$", RegexOptions.CultureInvariant)]
    private static partial Regex FieldNamePattern();
}
