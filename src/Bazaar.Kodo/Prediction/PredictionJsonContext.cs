using System.Text.Json;
using System.Text.Json.Serialization;

namespace Bazaar.Kodo.Prediction;

/// <summary>
/// Source-generated JSON for predictor memory. camelCase, compact, and unknown members are rejected so a damaged or
/// foreign file is not half-read. NaN and infinity are not valid JSON numbers here and fail to parse.
/// </summary>
[JsonSourceGenerationOptions(
    WriteIndented = false,
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.Never,
    UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow)]
[JsonSerializable(typeof(PredictionMemory))]
[JsonSerializable(typeof(TrendMemory))]
[JsonSerializable(typeof(BaselineMemory))]
[JsonSerializable(typeof(DriftMemory))]
internal sealed partial class PredictionJsonContext : JsonSerializerContext
{
}
