using System.Text.Json.Serialization;

namespace Bazaar.Client.CompanySetup;

internal sealed record IssueBody(string? Severity, string? Path, string? Message);

internal sealed record PlanReplyBody(int ExitCode, string? PlanHash, List<IssueBody>? Issues);

internal sealed record ApplyReplyBody(int ExitCode, string? PlanHash, List<string>? AppliedSteps, List<IssueBody>? Issues, bool Replayed);

/// <summary>Reads the provisioning API's answers (camelCase, as the server writes them). Source-generated, no reflection.</summary>
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(PlanReplyBody))]
[JsonSerializable(typeof(ApplyReplyBody))]
internal sealed partial class SetupJsonContext : JsonSerializerContext;
