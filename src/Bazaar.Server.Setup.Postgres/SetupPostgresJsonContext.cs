using System.Text.Json.Serialization;

namespace Bazaar.Server.Setup.Postgres;

/// <summary>Source-generated JSON for the stored apply result. Enums are written by name, so a stored row stays readable.</summary>
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    UseStringEnumConverter = true,
    WriteIndented = false)]
[JsonSerializable(typeof(SetupApplyResult))]
internal sealed partial class SetupPostgresJsonContext : JsonSerializerContext
{
}
