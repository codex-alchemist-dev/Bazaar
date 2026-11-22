using System.Text.Json.Serialization;

namespace Bazaar.Contracts.Git;

/// <summary>
/// Source-generated JSON metadata for the GitHub merge contracts. Options match the other contract contexts: camelCase, nulls
/// omitted, unknown members skipped.
/// </summary>
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    UnmappedMemberHandling = JsonUnmappedMemberHandling.Skip)]
[JsonSerializable(typeof(GitHubMergeRequestDto))]
[JsonSerializable(typeof(GitHubMergeResultDto))]
[JsonSerializable(typeof(GitHubMergeErrorDto))]
public sealed partial class GitHubContractsJsonContext : JsonSerializerContext;
