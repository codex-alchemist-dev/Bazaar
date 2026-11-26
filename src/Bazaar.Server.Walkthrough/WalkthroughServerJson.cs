using System.Text.Json.Serialization;

namespace Bazaar.Server.Walkthrough;

internal sealed record FactsReply(
    [property: JsonPropertyName("company_created")] bool CompanyCreated,
    [property: JsonPropertyName("first_agent_name")] string? FirstAgentName,
    [property: JsonPropertyName("first_task_given")] bool FirstTaskGiven,
    [property: JsonPropertyName("first_agent_id")] Guid? FirstAgentId = null,
    [property: JsonPropertyName("first_chat_id")] Guid? FirstChatId = null);

internal sealed record PrefsBody(
    [property: JsonPropertyName("dismissed")] bool Dismissed,
    [property: JsonPropertyName("finished")] bool Finished,
    [property: JsonPropertyName("current")] string? Current,
    [property: JsonPropertyName("completed")] string[]? Completed);

internal sealed record ErrorBody([property: JsonPropertyName("error")] string Error);

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.Unspecified)]
[JsonSerializable(typeof(FactsReply))]
[JsonSerializable(typeof(PrefsBody))]
[JsonSerializable(typeof(ErrorBody))]
internal sealed partial class WalkthroughServerJson : JsonSerializerContext;
