using System.Text.Json.Serialization;

namespace Bazaar.Contracts.Approvals;

/// <summary>
/// Source-generated JSON metadata for the approval contracts. Kept apart from <c>WireJsonContext</c>, which only the
/// wire work package edits. Options match that context: camelCase, nulls omitted, unknown members skipped.
/// </summary>
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    UnmappedMemberHandling = JsonUnmappedMemberHandling.Skip)]
[JsonSerializable(typeof(QuotedExcerptDto))]
[JsonSerializable(typeof(ApprovalCardDto))]
[JsonSerializable(typeof(ApprovalDecisionDto))]
public sealed partial class ApprovalContractsJsonContext : JsonSerializerContext;
