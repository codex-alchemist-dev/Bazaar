using System.Text.Json.Serialization;

namespace Bazaar.Contracts.Git;

/// <summary>Source-generated JSON metadata for the git read contracts: camelCase, nulls omitted, unknown members skipped.</summary>
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    UnmappedMemberHandling = JsonUnmappedMemberHandling.Skip)]
[JsonSerializable(typeof(GitPrPageDto))]
[JsonSerializable(typeof(GitPrDetailDto))]
[JsonSerializable(typeof(GitPrFileDto[]))]
[JsonSerializable(typeof(GitCheckDto[]))]
[JsonSerializable(typeof(GitHeadMovedDto))]
public sealed partial class GitReadContractsJsonContext : JsonSerializerContext;
