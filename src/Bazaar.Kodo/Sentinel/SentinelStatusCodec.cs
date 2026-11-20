using System.Text.Json;
using System.Text.Json.Serialization;

namespace Bazaar.Kodo.Sentinel;

/// <summary>
/// Source-generated JSON for the sentinel status file. Properties are written in declaration order, camelCase, with no
/// indentation, and nulls are written rather than dropped, so one status has one encoding.
/// </summary>
[JsonSourceGenerationOptions(
    WriteIndented = false,
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.Never)]
[JsonSerializable(typeof(SentinelStatus))]
[JsonSerializable(typeof(SentinelVerification))]
[JsonSerializable(typeof(SentinelRollback))]
internal sealed partial class SentinelJsonContext : JsonSerializerContext
{
}

/// <summary>Writes the sentinel status as the stable UTF-8 JSON the loopback status file carries.</summary>
public static class SentinelStatusCodec
{
    /// <summary>The UTF-8 JSON of <paramref name="status"/>.</summary>
    public static byte[] Encode(SentinelStatus status)
    {
        ArgumentNullException.ThrowIfNull(status);
        return JsonSerializer.SerializeToUtf8Bytes(status, SentinelJsonContext.Default.SentinelStatus);
    }
}
