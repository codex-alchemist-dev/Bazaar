using System.Text.Json;
using System.Text.Json.Serialization;

namespace Bazaar.Kodo.Api;

/// <summary>
/// Source-generated JSON for the read API and the <c>kodo</c> stream (doc 46 section 15). Properties are written in declaration
/// order, camelCase, with no indentation, and nulls are written rather than dropped, so one value has one encoding.
/// </summary>
[JsonSourceGenerationOptions(
    WriteIndented = false,
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.Never)]
[JsonSerializable(typeof(KodoStatusSnapshot))]
[JsonSerializable(typeof(KodoComponentStatus))]
[JsonSerializable(typeof(KodoStreamEvent))]
[JsonSerializable(typeof(KodoStateChange))]
internal sealed partial class KodoApiJsonContext : JsonSerializerContext
{
}

/// <summary>Writes the status snapshot and the stream events as the stable UTF-8 JSON the wire carries.</summary>
public static class KodoApiCodec
{
    /// <summary>The UTF-8 JSON of <paramref name="snapshot"/>.</summary>
    public static byte[] EncodeSnapshot(KodoStatusSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        return JsonSerializer.SerializeToUtf8Bytes(snapshot, KodoApiJsonContext.Default.KodoStatusSnapshot);
    }

    /// <summary>The UTF-8 JSON of one stream event.</summary>
    public static byte[] EncodeEvent(KodoStreamEvent streamEvent)
    {
        ArgumentNullException.ThrowIfNull(streamEvent);
        return JsonSerializer.SerializeToUtf8Bytes(streamEvent, KodoApiJsonContext.Default.KodoStreamEvent);
    }
}
