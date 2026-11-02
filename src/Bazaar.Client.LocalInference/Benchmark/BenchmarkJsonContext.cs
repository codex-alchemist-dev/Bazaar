using System.Text.Json;
using System.Text.Json.Serialization;

namespace Bazaar.Client.LocalInference.Benchmark;

/// <summary>Source-generated JSON for the corpus and the report (no reflection serializer).</summary>
[JsonSourceGenerationOptions(
    WriteIndented = true,
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    Converters = [typeof(JsonStringEnumConverter<BackendTier>)])]
[JsonSerializable(typeof(List<CorpusItem>))]
[JsonSerializable(typeof(BenchmarkReport))]
internal sealed partial class BenchmarkJsonContext : JsonSerializerContext;
