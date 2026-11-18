using System.Globalization;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Bazaar.Server.ReadAudit;

/// <summary>
/// The <c>data.read</c> audit row (doc 48 section 5.1): who read, which object class (and id, for a personal object), how many
/// rows, the taint tags, the purpose and the time window. Fields are fixed keys whose values are validated
/// ids or labels, so a payload cannot carry row content, SQL text or a secret. Canonical JSON, keys sorted.
/// </summary>
public static partial class DataReadPayload
{
    /// <summary>Audit kind of a read row.</summary>
    public const string Kind = "data.read";

    /// <summary>Principal used when a read has no scope.</summary>
    public const string UnattributedPrincipal = "unattributed";

    /// <summary>Route used when a read has no scope.</summary>
    public const string UnattributedRoute = "unattributed";

    /// <summary>Validates the context of a scope. Throws <see cref="ArgumentException"/> when a field is not an id or a label.</summary>
    public static void ValidateContext(ReadAccessContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        RequireId(context.Principal, nameof(context.Principal));
        RequireLabel(context.Route, nameof(context.Route));
        if (context.Session is not null)
            RequireId(context.Session, nameof(context.Session));
        if (context.Purpose is not null)
            RequireLabel(context.Purpose, nameof(context.Purpose));
        if (context.TaintTags is not null)
        {
            foreach (var tag in context.TaintTags)
                RequireLabel(tag, nameof(context.TaintTags));
        }
    }

    /// <summary>Validates one read. A personal read has exactly one object and a count of 1.</summary>
    public static void ValidateRead(DataRead read)
    {
        ArgumentNullException.ThrowIfNull(read);
        RequireLabel(read.ObjectType, nameof(read.ObjectType));
        if (read.ResultCount < 0)
            throw new ArgumentOutOfRangeException(nameof(read), read.ResultCount, "A read cannot return a negative row count.");
        if (read.QueryKind is not null)
            RequireLabel(read.QueryKind, nameof(read.QueryKind));
        if (read.QueryShapeHash is not null && !HashPattern().IsMatch(read.QueryShapeHash))
            throw new ArgumentException("Query shape must be a lowercase hex SHA-256.", nameof(read));
        if (read.ObjectId is not null)
        {
            RequireId(read.ObjectId, nameof(read.ObjectId));
            if (read.ResultCount != 1)
                throw new ArgumentException("A personal object read is one object, so its count must be 1.", nameof(read));
        }
    }

    /// <summary>Builds the canonical payload for one row. The timestamps are UTC, to the microsecond.</summary>
    internal static JsonObject Build(ReadAccessContext context, DataReadRow row)
    {
        var fields = new JsonObject
        {
            ["principal"] = context.Principal,
            ["session"] = context.Session,
            ["route"] = context.Route,
            ["purpose"] = context.Purpose,
            ["taint_tags"] = new JsonArray((context.TaintTags ?? []).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal)
                .Select(t => (JsonNode)t).ToArray()),
            ["object_type"] = row.ObjectType,
            ["object_id"] = row.ObjectId,
            ["query_kind"] = row.QueryKind,
            ["result_count"] = row.ResultCount,
            ["read_from"] = Timestamp(row.From),
            ["read_to"] = Timestamp(row.To),
        };
        return Canonicalize(fields);
    }

    private static string Timestamp(DateTimeOffset value) =>
        value.ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ss.ffffffZ", CultureInfo.InvariantCulture);

    private static void RequireId(string value, string paramName)
    {
        ArgumentException.ThrowIfNullOrEmpty(value, paramName);
        if (!IdPattern().IsMatch(value))
            throw new ArgumentException($"'{paramName}' must be an id of letters, digits and _ . : @ - (1 to 128 characters).", paramName);
    }

    private static void RequireLabel(string value, string paramName)
    {
        ArgumentException.ThrowIfNullOrEmpty(value, paramName);
        if (!LabelPattern().IsMatch(value))
            throw new ArgumentException($"'{paramName}' must be a lowercase label of letters, digits and _ . : / - (1 to 128 characters).", paramName);
    }

    private static JsonObject Canonicalize(JsonObject obj)
    {
        var sorted = new JsonObject();
        foreach (var name in obj.Select(kv => kv.Key).Order(StringComparer.Ordinal))
            sorted[name] = CanonicalizeNode(obj[name]);
        return sorted;
    }

    private static JsonNode? CanonicalizeNode(JsonNode? node) => node switch
    {
        null => null,
        JsonObject obj => Canonicalize(obj),
        JsonArray arr => new JsonArray(arr.Select(CanonicalizeNode).ToArray()),
        _ => JsonNode.Parse(node.ToJsonString()),
    };

    [GeneratedRegex("^[A-Za-z0-9][A-Za-z0-9_.:@\\-]{0,127}$")]
    private static partial Regex IdPattern();

    [GeneratedRegex("^[a-z0-9][a-z0-9_./:\\-]{0,127}$")]
    private static partial Regex LabelPattern();

    [GeneratedRegex("^[0-9a-f]{64}$")]
    private static partial Regex HashPattern();
}

/// <summary>One audit row to write: a single personal object, or a batched count for one query shape.</summary>
internal sealed record DataReadRow(string ObjectType, string? ObjectId, string? QueryKind, long ResultCount, DateTimeOffset From, DateTimeOffset To);
