using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Bazaar.Kodo.Reports;

/// <summary>
/// Source-generated JSON for reports and their journal entries. camelCase, compact, and unknown members are rejected so a damaged
/// or foreign document is not half-read.
/// </summary>
[JsonSourceGenerationOptions(
    WriteIndented = false,
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.Never,
    UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow)]
[JsonSerializable(typeof(HealthReport))]
[JsonSerializable(typeof(ReportRunJournalPayload))]
internal sealed partial class ReportJsonContext : JsonSerializerContext
{
}

/// <summary>Encodes a report to its canonical bytes and takes its digest. A signer, when one exists, signs the digest.</summary>
public static class HealthReportCodec
{
    /// <summary>The most bytes <see cref="Decode"/> will parse.</summary>
    public const int MaxBytes = 1024 * 1024;

    /// <summary>The canonical UTF-8 JSON bytes: fixed member order, no whitespace.</summary>
    public static byte[] Encode(HealthReport report)
    {
        ArgumentNullException.ThrowIfNull(report);
        return JsonSerializer.SerializeToUtf8Bytes(report, ReportJsonContext.Default.HealthReport);
    }

    /// <summary>The lowercase hex SHA-256 of the canonical bytes.</summary>
    public static string Digest(HealthReport report) => Convert.ToHexString(SHA256.HashData(Encode(report))).ToLowerInvariant();

    /// <summary>Parses a report, or returns null when the bytes are too big, malformed, or not a report of the current schema version.</summary>
    public static HealthReport? Decode(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length is 0 or > MaxBytes)
        {
            return null;
        }

        try
        {
            HealthReport? report = JsonSerializer.Deserialize(bytes, ReportJsonContext.Default.HealthReport);
            return report is { SchemaVersion: HealthReport.CurrentSchemaVersion } ? report : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
