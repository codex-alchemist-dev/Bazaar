using System.Text.Json.Serialization;

namespace Bazaar.Testing.Replay;

/// <summary>The kinds of record a transcript replay fixture may hold.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<ReplayKind>))]
public enum ReplayKind
{
    [JsonStringEnumMemberName("gc_message")]
    GcMessage,

    [JsonStringEnumMemberName("tool_call")]
    ToolCall,

    [JsonStringEnumMemberName("model_request")]
    ModelRequest,

    [JsonStringEnumMemberName("model_response")]
    ModelResponse,

    [JsonStringEnumMemberName("wake")]
    Wake,
}

/// <summary>Per-kind rules: the wire name and the structural keys the comparator checks.</summary>
public static class ReplayKinds
{
    /// <summary>The name used in fixture files.</summary>
    public static string WireName(this ReplayKind kind) => kind switch
    {
        ReplayKind.GcMessage => "gc_message",
        ReplayKind.ToolCall => "tool_call",
        ReplayKind.ModelRequest => "model_request",
        ReplayKind.ModelResponse => "model_response",
        ReplayKind.Wake => "wake",
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, null),
    };

    /// <summary>
    /// The keys every record of this kind must carry in its <c>keys</c> object, and the only keys the comparator
    /// reads. Ids, timestamps and text are never structural.
    /// </summary>
    public static IReadOnlyList<string> StructuralKeys(this ReplayKind kind) => kind switch
    {
        ReplayKind.GcMessage => ["speaker"],
        ReplayKind.ToolCall => ["tool"],
        ReplayKind.ModelRequest => ["model"],
        ReplayKind.ModelResponse => ["model"],
        ReplayKind.Wake => ["agent"],
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, null),
    };
}

/// <summary>
/// One record of a transcript. <see cref="Seq"/> is the record's position in the recorded transcript; the loader
/// requires it to rise strictly through the file. <see cref="Id"/>, <see cref="At"/> and <see cref="Text"/> are kept
/// for humans reading a fixture and are never compared.
/// </summary>
public sealed record ReplayRecord(
    [property: JsonPropertyName("seq")] long Seq,
    [property: JsonPropertyName("kind")] ReplayKind Kind,
    [property: JsonPropertyName("keys")] IReadOnlyDictionary<string, string> Keys,
    [property: JsonPropertyName("id")] string? Id = null,
    [property: JsonPropertyName("at")] string? At = null,
    [property: JsonPropertyName("text")] string? Text = null);

/// <summary>A sanitized, ordered transcript: the unit the structural comparator checks.</summary>
public sealed record ReplayFixture(
    [property: JsonPropertyName("format")] int Format,
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("records")] IReadOnlyList<ReplayRecord> Records)
{
    /// <summary>The only fixture format this build reads.</summary>
    public const int CurrentFormat = 1;
}
