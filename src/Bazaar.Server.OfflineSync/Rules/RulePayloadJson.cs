using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;

namespace Bazaar.Server.OfflineSync.Rules;

/// <summary>Payload of a GC message or a GC message edit.</summary>
/// <param name="Body">The message text.</param>
public sealed record GcMessagePayload(string Body);

/// <summary>Payload of a Kanban field write.</summary>
/// <param name="Field">The field name, for example "status".</param>
/// <param name="Value">The new field value.</param>
public sealed record KanbanFieldPayload(string Field, string Value);

/// <summary>Payload of a Kanban dependency edge add or remove.</summary>
/// <param name="FromTaskId">The task the edge leaves.</param>
/// <param name="ToTaskId">The task the edge enters.</param>
/// <param name="Remove">True to remove the edge; false to add it.</param>
public sealed record KanbanEdgePayload(string FromTaskId, string ToTaskId, bool Remove);

/// <summary>Payload of a Constellation node position.</summary>
/// <param name="X">Horizontal position.</param>
/// <param name="Y">Vertical position.</param>
public sealed record ConstellationPositionPayload(double X, double Y);

/// <summary>Payload of a user preference value.</summary>
/// <param name="Value">The preference value, encoded as text.</param>
public sealed record PreferencePayload(string Value);

/// <summary>JSON source generation context for rule payloads. AOT safe, no reflection.</summary>
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, WriteIndented = false)]
[JsonSerializable(typeof(GcMessagePayload))]
[JsonSerializable(typeof(KanbanFieldPayload))]
[JsonSerializable(typeof(KanbanEdgePayload))]
[JsonSerializable(typeof(ConstellationPositionPayload))]
[JsonSerializable(typeof(PreferencePayload))]
public sealed partial class RulePayloadJsonContext : JsonSerializerContext
{
}

/// <summary>Decodes rule payloads. Returns null for malformed bytes instead of throwing.</summary>
public static class RulePayloadCodec
{
    /// <summary>Decodes a GC message payload.</summary>
    public static GcMessagePayload? ReadGcMessage(ReadOnlyMemory<byte> bytes) =>
        Read(bytes, RulePayloadJsonContext.Default.GcMessagePayload);

    /// <summary>Decodes a Kanban field payload.</summary>
    public static KanbanFieldPayload? ReadKanbanField(ReadOnlyMemory<byte> bytes) =>
        Read(bytes, RulePayloadJsonContext.Default.KanbanFieldPayload);

    /// <summary>Decodes a Kanban edge payload.</summary>
    public static KanbanEdgePayload? ReadKanbanEdge(ReadOnlyMemory<byte> bytes) =>
        Read(bytes, RulePayloadJsonContext.Default.KanbanEdgePayload);

    /// <summary>Decodes a Constellation position payload.</summary>
    public static ConstellationPositionPayload? ReadConstellationPosition(ReadOnlyMemory<byte> bytes) =>
        Read(bytes, RulePayloadJsonContext.Default.ConstellationPositionPayload);

    /// <summary>Decodes a preference payload.</summary>
    public static PreferencePayload? ReadPreference(ReadOnlyMemory<byte> bytes) =>
        Read(bytes, RulePayloadJsonContext.Default.PreferencePayload);

    private static T? Read<T>(ReadOnlyMemory<byte> bytes, JsonTypeInfo<T> info) where T : class
    {
        try
        {
            return JsonSerializer.Deserialize(bytes.Span, info);
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
