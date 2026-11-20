using System.Text.Json;

namespace Bazaar.Contracts.Kodo;

/// <summary>
/// Reads and writes the Kodo snapshot and stream events as UTF-8 JSON through the source-generated
/// <see cref="KodoContractsJsonContext"/>, so one value has one encoding.
/// </summary>
public static class KodoWireCodec
{
    /// <summary>The UTF-8 JSON of <paramref name="snapshot"/>.</summary>
    public static byte[] EncodeSnapshot(KodoStatusSnapshotDto snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        return JsonSerializer.SerializeToUtf8Bytes(snapshot, KodoContractsJsonContext.Default.KodoStatusSnapshotDto);
    }

    /// <summary>The UTF-8 JSON of one stream event.</summary>
    public static byte[] EncodeEvent(KodoStreamEventDto streamEvent)
    {
        ArgumentNullException.ThrowIfNull(streamEvent);
        return JsonSerializer.SerializeToUtf8Bytes(streamEvent, KodoContractsJsonContext.Default.KodoStreamEventDto);
    }

    /// <summary>Reads a snapshot, or null for a JSON null.</summary>
    public static KodoStatusSnapshotDto? DecodeSnapshot(ReadOnlySpan<byte> utf8Json) =>
        JsonSerializer.Deserialize(utf8Json, KodoContractsJsonContext.Default.KodoStatusSnapshotDto);

    /// <summary>Reads one stream event, or null for a JSON null.</summary>
    public static KodoStreamEventDto? DecodeEvent(ReadOnlySpan<byte> utf8Json) =>
        JsonSerializer.Deserialize(utf8Json, KodoContractsJsonContext.Default.KodoStreamEventDto);
}
