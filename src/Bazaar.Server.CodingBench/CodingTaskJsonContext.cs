using System.Text.Json;
using System.Text.Json.Serialization;
using Bazaar.Server.CodingBench.Abstractions;

namespace Bazaar.Server.CodingBench;

/// <summary>Source-generated JSON for the pinned task file: camelCase properties, enums written and read as strings.</summary>
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    WriteIndented = true,
    Converters = new[]
    {
        typeof(JsonStringEnumConverter<CodingTaskKind>),
        typeof(JsonStringEnumConverter<ExpectedOutcome>),
        typeof(JsonStringEnumConverter<BenchEditFormat>),
    })]
[JsonSerializable(typeof(List<CodingTask>))]
internal sealed partial class CodingTaskJsonContext : JsonSerializerContext
{
}
