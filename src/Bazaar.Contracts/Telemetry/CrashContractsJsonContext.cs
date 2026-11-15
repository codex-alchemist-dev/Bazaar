using System.Text.Json.Serialization;

namespace Bazaar.Contracts.Telemetry;

/// <summary>
/// Source-generated JSON metadata for crash reports and their wire payloads (doc 43, section 5). Names are snake_case, as
/// doc 43 section 5.1 shows them. Nulls are written, so the report keeps every field of its schema. Unknown members are
/// skipped on read, so a newer server can add fields without breaking older clients.
/// </summary>
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower,
    UnmappedMemberHandling = JsonUnmappedMemberHandling.Skip)]
[JsonSerializable(typeof(CrashReport))]
[JsonSerializable(typeof(IReadOnlyList<CrashLogEntry>))]
[JsonSerializable(typeof(CrashSubmit))]
[JsonSerializable(typeof(CrashAck))]
[JsonSerializable(typeof(CrashAttachmentBegin))]
[JsonSerializable(typeof(CrashAttachmentChunk))]
[JsonSerializable(typeof(CrashAttachmentEnd))]
public sealed partial class CrashContractsJsonContext : JsonSerializerContext;
