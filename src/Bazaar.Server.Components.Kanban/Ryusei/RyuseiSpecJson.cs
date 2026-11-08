using System.Globalization;
using System.Text.Json.Nodes;
using System.Xml;

namespace Bazaar.Server.Kanban.Ryusei;

/// <summary>
/// Reads and writes the <c>spec</c> column (doc 38, Trigger types). Durations use ISO 8601 (<c>PT30M</c>) and instants use
/// the round-trip form. Decoding rejects a document that does not match its kind; it never guesses a default.
/// </summary>
public static class RyuseiSpecJson
{
    public static string Encode(RyuseiTriggerSpec spec)
    {
        ArgumentNullException.ThrowIfNull(spec);
        JsonObject node = spec switch
        {
            RyuseiTimerTriggerSpec timer => new JsonObject { ["fireAt"] = Instant(timer.FireAt) },
            RyuseiIntervalTriggerSpec interval => new JsonObject
            {
                ["every"] = XmlConvert.ToString(interval.Every),
                ["anchor"] = Instant(interval.Anchor),
            },
            RyuseiWatcherTriggerSpec watcher => Watcher(watcher),
            RyuseiEventTriggerSpec evt => new JsonObject { ["predicate"] = Predicate(evt.Predicate) },
            _ => throw new ArgumentOutOfRangeException(nameof(spec), spec, "Unknown spec type."),
        };
        return node.ToJsonString();
    }

    public static RyuseiTriggerSpec Decode(RyuseiTriggerKind kind, string json)
    {
        ArgumentException.ThrowIfNullOrEmpty(json);
        var node = JsonNode.Parse(json) as JsonObject
            ?? throw new InvalidDataException("A trigger spec must be a JSON object.");

        return kind switch
        {
            RyuseiTriggerKind.Timer => new RyuseiTimerTriggerSpec(ReadInstant(node, "fireAt")),
            RyuseiTriggerKind.Interval => new RyuseiIntervalTriggerSpec(
                ReadDuration(node, "every"),
                ReadInstant(node, "anchor")),
            RyuseiTriggerKind.Watcher => new RyuseiWatcherTriggerSpec(
                ReadPredicate(RequiredObject(node, "predicate")),
                ReadInt(node, "pollSeconds"),
                ReadInt(node, "debounceSeconds"),
                ReadOptionalInstant(node, "expiresAt"),
                ReadOptionalInt(node, "maxFires")),
            RyuseiTriggerKind.Event => new RyuseiEventTriggerSpec(ReadPredicate(RequiredObject(node, "predicate"))),
            _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "Unknown trigger kind."),
        };
    }

    private static JsonObject Watcher(RyuseiWatcherTriggerSpec watcher)
    {
        var node = new JsonObject
        {
            ["predicate"] = Predicate(watcher.Predicate),
            ["pollSeconds"] = watcher.PollSeconds,
            ["debounceSeconds"] = watcher.DebounceSeconds,
        };
        if (watcher.ExpiresAt is { } expiresAt)
        {
            node["expiresAt"] = Instant(expiresAt);
        }

        if (watcher.MaxFires is { } maxFires)
        {
            node["maxFires"] = maxFires;
        }

        return node;
    }

    private static JsonObject Predicate(RyuseiPredicate predicate)
    {
        var node = new JsonObject { ["kind"] = predicate.Kind };
        if (predicate.GcId is { } gcId)
        {
            node["gcId"] = gcId.ToString("D");
        }

        if (predicate.AuthorAgentId is { } author)
        {
            node["authorAgentId"] = author.ToString("D");
        }

        if (predicate.PathGlob is { } glob)
        {
            node["pathGlob"] = glob;
        }

        return node;
    }

    private static RyuseiPredicate ReadPredicate(JsonObject node) => new()
    {
        Kind = ReadString(node, "kind"),
        GcId = ReadOptionalGuid(node, "gcId"),
        AuthorAgentId = ReadOptionalGuid(node, "authorAgentId"),
        PathGlob = node["pathGlob"] is null ? null : ReadString(node, "pathGlob"),
    };

    private static string Instant(DateTimeOffset value) => value.ToString("O", CultureInfo.InvariantCulture);

    private static JsonObject RequiredObject(JsonObject node, string name) =>
        node[name] as JsonObject ?? throw new InvalidDataException($"Spec field '{name}' must be an object.");

    private static string ReadString(JsonObject node, string name) =>
        node[name]?.GetValue<string>() ?? throw new InvalidDataException($"Spec field '{name}' is required.");

    private static int ReadInt(JsonObject node, string name) =>
        node[name]?.GetValue<int>() ?? throw new InvalidDataException($"Spec field '{name}' is required.");

    private static int? ReadOptionalInt(JsonObject node, string name) =>
        node[name] is null ? null : node[name]!.GetValue<int>();

    private static DateTimeOffset ReadInstant(JsonObject node, string name) =>
        DateTimeOffset.Parse(ReadString(node, name), CultureInfo.InvariantCulture, DateTimeStyles.None);

    private static DateTimeOffset? ReadOptionalInstant(JsonObject node, string name) =>
        node[name] is null ? null : ReadInstant(node, name);

    private static TimeSpan ReadDuration(JsonObject node, string name) =>
        XmlConvert.ToTimeSpan(ReadString(node, name));

    private static Guid? ReadOptionalGuid(JsonObject node, string name) =>
        node[name] is null ? null : Guid.Parse(ReadString(node, name));
}
