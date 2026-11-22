using System.Text.Json.Serialization;
using Bazaar.Server.Setup;

namespace Bazaar.Admin;

/// <summary>The JSON document for <c>--json</c>. Source-generated, so the output is AOT-safe and uses no reflection.</summary>
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.Never,
    WriteIndented = true)]
[JsonSerializable(typeof(SetupReport))]
[JsonSerializable(typeof(SetupReportIssue))]
[JsonSerializable(typeof(SetupReportChange))]
[JsonSerializable(typeof(KodoVerifyReport))]
[JsonSerializable(typeof(AdminCheck))]
public sealed partial class AdminJsonContext : JsonSerializerContext
{
}
