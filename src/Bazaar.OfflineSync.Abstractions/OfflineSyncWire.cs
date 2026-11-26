using System.Text.Json.Serialization;

namespace Bazaar.OfflineSync;

/// <summary>The JSON body of <c>POST /offline-sync/batch</c> (doc 56 step 3). The payload travels as base64 text.</summary>
/// <param name="DeviceId">The writing device; the server refuses a batch whose id is not the authenticated device.</param>
/// <param name="Writes">The writes in device-sequence order.</param>
public sealed record OfflineBatchRequestDto(
    [property: JsonPropertyName("device_id")] Guid DeviceId,
    [property: JsonPropertyName("writes")] IReadOnlyList<OfflineWriteDto> Writes);

/// <summary>One write on the wire. See <see cref="OfflineWrite"/> for the meaning of each field.</summary>
public sealed record OfflineWriteDto(
    [property: JsonPropertyName("write_id")] Guid WriteId,
    [property: JsonPropertyName("device_id")] Guid DeviceId,
    [property: JsonPropertyName("device_sequence")] long DeviceSequence,
    [property: JsonPropertyName("data_class")] OfflineDataClass DataClass,
    [property: JsonPropertyName("target")] string Target,
    [property: JsonPropertyName("base_version")] string? BaseVersion,
    [property: JsonPropertyName("payload")] string PayloadBase64,
    [property: JsonPropertyName("hlc_wall_millis")] long HlcWallMillis,
    [property: JsonPropertyName("hlc_counter")] int HlcCounter,
    [property: JsonPropertyName("hlc_device_id")] Guid HlcDeviceId);

/// <summary>One write outcome on the wire.</summary>
public sealed record OfflineOutcomeDto(
    [property: JsonPropertyName("write_id")] Guid WriteId,
    [property: JsonPropertyName("kind")] OfflineOutcomeKind Kind,
    [property: JsonPropertyName("server_version")] string? ServerVersion,
    [property: JsonPropertyName("reason")] string? Reason);

/// <summary>The JSON response of <c>POST /offline-sync/batch</c>.</summary>
public sealed record OfflineBatchResponseDto(
    [property: JsonPropertyName("outcomes")] IReadOnlyList<OfflineOutcomeDto> Outcomes,
    [property: JsonPropertyName("acked_sequence")] long AckedSequence,
    [property: JsonPropertyName("gap_at_sequence")] long? GapAtSequence);

/// <summary>Conversions between the domain shapes and the wire DTOs.</summary>
public static class OfflineSyncWire
{
    /// <summary>The route path, relative to the server base address, without a leading slash.</summary>
    public const string BatchRoute = "offline-sync/batch";

    /// <summary>Converts a batch to its wire form.</summary>
    public static OfflineBatchRequestDto ToDto(OfflineWriteBatch batch)
    {
        ArgumentNullException.ThrowIfNull(batch);
        return new OfflineBatchRequestDto(batch.DeviceId, [.. batch.Writes.Select(static w => new OfflineWriteDto(
            w.WriteId, w.DeviceId, w.DeviceSequence, w.DataClass, w.Target, w.BaseVersion,
            Convert.ToBase64String(w.Payload.Span), w.Hlc.WallMillis, w.Hlc.Counter, w.Hlc.DeviceId))]);
    }

    /// <summary>Converts a wire batch to the domain batch. Returns null when any payload is not valid base64 or the shape is incomplete.</summary>
    public static OfflineWriteBatch? ToBatch(OfflineBatchRequestDto? dto)
    {
        if (dto?.Writes is null)
        {
            return null;
        }

        var writes = new List<OfflineWrite>(dto.Writes.Count);
        foreach (var w in dto.Writes)
        {
            if (w is null || w.Target is null || w.PayloadBase64 is null)
            {
                return null;
            }

            var payload = new byte[(w.PayloadBase64.Length * 3 / 4) + 3];
            if (!Convert.TryFromBase64String(w.PayloadBase64, payload, out var written))
            {
                return null;
            }

            writes.Add(new OfflineWrite(
                w.WriteId, w.DeviceId, w.DeviceSequence, w.DataClass, w.Target, w.BaseVersion,
                payload.AsMemory(0, written), new Hlc(w.HlcWallMillis, w.HlcCounter, w.HlcDeviceId)));
        }

        return new OfflineWriteBatch(dto.DeviceId, writes);
    }

    /// <summary>Converts a batch result to its wire form.</summary>
    public static OfflineBatchResponseDto ToDto(OfflineBatchResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        return new OfflineBatchResponseDto(
            [.. result.Outcomes.Select(static o => new OfflineOutcomeDto(o.WriteId, o.Kind, o.ServerVersion, o.Reason))],
            result.AckedSequence,
            result.GapAtSequence);
    }

    /// <summary>Converts a wire response to the domain result.</summary>
    public static OfflineBatchResult ToResult(OfflineBatchResponseDto dto)
    {
        ArgumentNullException.ThrowIfNull(dto);
        return new OfflineBatchResult(
            [.. dto.Outcomes.Select(static o => new OfflineWriteOutcome(o.WriteId, o.Kind, o.ServerVersion, o.Reason))],
            dto.AckedSequence,
            dto.GapAtSequence);
    }
}

/// <summary>Source-generated JSON for the offline sync wire bodies (no reflection at runtime, per the wire rules).</summary>
[JsonSerializable(typeof(OfflineBatchRequestDto))]
[JsonSerializable(typeof(OfflineBatchResponseDto))]
[JsonSourceGenerationOptions(PropertyNameCaseInsensitive = true, Converters = [typeof(JsonStringEnumConverter<OfflineDataClass>), typeof(JsonStringEnumConverter<OfflineOutcomeKind>)])]
public sealed partial class OfflineSyncWireContext : JsonSerializerContext
{
}
