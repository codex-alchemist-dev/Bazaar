using System.Text.Json.Serialization;

namespace Bazaar.Client.Walkthrough;

/// <summary>Body of <c>GET /onboarding/walkthrough/facts</c> (PROPOSED, doc 45 section 2.7).</summary>
internal sealed record FactsBody(
    [property: JsonPropertyName("company_created")] bool CompanyCreated,
    [property: JsonPropertyName("first_agent_name")] string? FirstAgentName,
    [property: JsonPropertyName("first_task_given")] bool FirstTaskGiven);

/// <summary>Body of <c>GET</c> and <c>PUT /prefs/walkthrough</c> (PROPOSED, doc 45 section 2.7).</summary>
internal sealed record ProgressBody(
    [property: JsonPropertyName("dismissed")] bool Dismissed,
    [property: JsonPropertyName("finished")] bool Finished,
    [property: JsonPropertyName("current")] string? Current,
    [property: JsonPropertyName("completed")] string[]? Completed);

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.Unspecified)]
[JsonSerializable(typeof(FactsBody))]
[JsonSerializable(typeof(ProgressBody))]
internal sealed partial class WalkthroughJsonContext : JsonSerializerContext;
