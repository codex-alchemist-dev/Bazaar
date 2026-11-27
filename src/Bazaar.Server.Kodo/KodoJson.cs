using System.Text.Json.Serialization;

namespace Bazaar.Server.Kodo;

/// <summary>The one error shape of the Kodo read API (doc 46 section 15.1).</summary>
internal sealed record KodoErrorResponse(string Code, string Message, bool Retryable, string CorrelationId);

/// <summary>Journal events response: the head hash and the newest entries.</summary>
internal sealed record KodoEventsResponse(int Schema, string? Head, IReadOnlyList<KodoJournalEntryView> Entries);

/// <summary>Merkle proof response.</summary>
internal sealed record KodoProofResponse(int Schema, KodoJournalProofView Proof);

/// <summary>Checkpoints with their anchor marks, newest first.</summary>
internal sealed record KodoAnchorsResponse(int Schema, IReadOnlyList<KodoCheckpointView> Checkpoints);

/// <summary>Source-generated JSON for the Kodo read endpoints (camelCase like the contracts, nulls kept out, no reflection).</summary>
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(KodoErrorResponse))]
[JsonSerializable(typeof(KodoEventsResponse))]
[JsonSerializable(typeof(KodoProofResponse))]
[JsonSerializable(typeof(KodoAnchorsResponse))]
internal sealed partial class KodoJsonContext : JsonSerializerContext;
