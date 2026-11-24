using System.Text.Json;
using System.Text.Json.Serialization;

namespace Bazaar.Server.Health;

/// <summary>Source-generated JSON for the health endpoint (snake_case, enums as strings, no reflection).</summary>
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower,
    UseStringEnumConverter = true,
    DefaultIgnoreCondition = JsonIgnoreCondition.Never)]
[JsonSerializable(typeof(ProjectHealthReport))]
[JsonSerializable(typeof(HealthReportRequest))]
internal sealed partial class HealthJsonContext : JsonSerializerContext;
