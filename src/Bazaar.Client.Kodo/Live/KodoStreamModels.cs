using System.Text.Json;
using System.Text.Json.Serialization;

namespace Bazaar.Client.Kodo.Live;

/// <summary>One component's state as the server sent it (doc 46 section 15.1). Strings stay as sent until the tracker checks them.</summary>
public sealed record KodoComponentWire(string Id, string State, string? Cause, DateTimeOffset Since);

/// <summary>The status snapshot on the wire (doc 46 sections 12.2 and 15.1).</summary>
public sealed record KodoSnapshotWire(
    int SchemaVersion,
    long Revision,
    DateTimeOffset GeneratedAt,
    string State,
    string Underlying,
    int Score,
    string? JournalHead,
    IReadOnlyList<KodoComponentWire> Components);

/// <summary>A change of the displayed state (doc 46 section 15.2).</summary>
public sealed record KodoStateChangeWire(string Previous, string State, string Underlying);

/// <summary>One frame of the <c>kodo</c> stream: a state change, a snapshot or a heartbeat.</summary>
public sealed record KodoFrameWire(long Revision, string Type, DateTimeOffset At, KodoStateChangeWire? StateChange, KodoSnapshotWire? Snapshot);

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, UnmappedMemberHandling = JsonUnmappedMemberHandling.Skip)]
[JsonSerializable(typeof(KodoSnapshotWire))]
[JsonSerializable(typeof(KodoFrameWire))]
internal sealed partial class KodoStreamJsonContext : JsonSerializerContext
{
}

/// <summary>Reads the read API's snapshot and the stream's frames. A body it cannot read yields false and a reason, never a guess.</summary>
public static class KodoStreamCodec
{
    /// <summary>Reads one snapshot body.</summary>
    public static bool TryReadSnapshot(ReadOnlySpan<byte> utf8, out KodoSnapshotWire? snapshot, out string? error) =>
        TryRead(utf8, KodoStreamJsonContext.Default.KodoSnapshotWire, out snapshot, out error);

    /// <summary>Reads one stream frame.</summary>
    public static bool TryReadFrame(ReadOnlySpan<byte> utf8, out KodoFrameWire? frame, out string? error) =>
        TryRead(utf8, KodoStreamJsonContext.Default.KodoFrameWire, out frame, out error);

    private static bool TryRead<T>(ReadOnlySpan<byte> utf8, System.Text.Json.Serialization.Metadata.JsonTypeInfo<T> info, out T? value, out string? error)
        where T : class
    {
        try
        {
            value = JsonSerializer.Deserialize(utf8, info);
            error = value is null ? "The Kodo body was empty." : null;
            return value is not null;
        }
        catch (JsonException ex)
        {
            value = null;
            error = $"The Kodo body could not be read: {ex.Message}";
            return false;
        }
    }
}
