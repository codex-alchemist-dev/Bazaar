using System.Text.Json;
using System.Text.Json.Serialization;
using Bazaar.Contracts.Gateway;

namespace Bazaar.Server.Gateway.Adapters.OpenAi;

/// <summary>Source-generated JSON for the OpenAI chat completions wire format. Property names are snake_case on the wire.</summary>
[JsonSourceGenerationOptions(
    JsonSerializerDefaults.Web,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower)]
[JsonSerializable(typeof(OpenAiChatRequest))]
[JsonSerializable(typeof(OpenAiChatChunk))]
internal sealed partial class OpenAiChatJsonContext : JsonSerializerContext;

internal sealed class OpenAiChatRequest
{
    public string Model { get; set; } = "";

    public bool Stream { get; set; }

    public OpenAiStreamOptions? StreamOptions { get; set; }

    public List<OpenAiChatMessage> Messages { get; set; } = [];

    public List<OpenAiChatTool>? Tools { get; set; }

    public int? MaxTokens { get; set; }

    public double? Temperature { get; set; }
}

internal sealed class OpenAiStreamOptions
{
    public bool IncludeUsage { get; set; }
}

internal sealed class OpenAiChatMessage
{
    public string Role { get; set; } = "";

    /// <summary>Omitted (null) on an assistant message that only carries tool calls.</summary>
    public string? Content { get; set; }

    public string? ToolCallId { get; set; }

    public List<OpenAiToolCall>? ToolCalls { get; set; }
}

internal sealed class OpenAiToolCall
{
    public string Id { get; set; } = "";

    public string Type { get; set; } = "function";

    public OpenAiFunctionCall Function { get; set; } = new();
}

internal sealed class OpenAiFunctionCall
{
    public string Name { get; set; } = "";

    /// <summary>Arguments are a JSON document sent as a string, as the wire format requires.</summary>
    public string Arguments { get; set; } = "";
}

internal sealed class OpenAiChatTool
{
    public string Type { get; set; } = "function";

    public OpenAiFunctionDefinition Function { get; set; } = new();
}

internal sealed class OpenAiFunctionDefinition
{
    public string Name { get; set; } = "";

    public string Description { get; set; } = "";

    public JsonElement Parameters { get; set; }
}

/// <summary>One streamed chunk. Only the fields the adapter reads are declared; unknown fields are ignored.</summary>
internal sealed class OpenAiChatChunk
{
    public string? Model { get; set; }

    public List<OpenAiChunkChoice>? Choices { get; set; }

    public OpenAiChunkUsage? Usage { get; set; }

    /// <summary>Some providers report a failure as an in-stream error object.</summary>
    public JsonElement? Error { get; set; }
}

internal sealed class OpenAiChunkChoice
{
    public OpenAiChunkDelta? Delta { get; set; }

    public string? FinishReason { get; set; }
}

internal sealed class OpenAiChunkDelta
{
    public string? Content { get; set; }

    public string? ReasoningContent { get; set; }

    /// <summary>Alternative name for reasoning text used by some OpenAI-compatible servers.</summary>
    public string? Reasoning { get; set; }

    public List<OpenAiChunkToolCall>? ToolCalls { get; set; }
}

internal sealed class OpenAiChunkToolCall
{
    public int Index { get; set; }

    public string? Id { get; set; }

    public OpenAiChunkFunction? Function { get; set; }
}

internal sealed class OpenAiChunkFunction
{
    public string? Name { get; set; }

    public string? Arguments { get; set; }
}

internal sealed class OpenAiChunkUsage
{
    public long? PromptTokens { get; set; }

    public long? CompletionTokens { get; set; }

    public OpenAiPromptDetails? PromptTokensDetails { get; set; }

    public OpenAiCompletionDetails? CompletionTokensDetails { get; set; }
}

internal sealed class OpenAiPromptDetails
{
    public long? CachedTokens { get; set; }
}

internal sealed class OpenAiCompletionDetails
{
    public long? ReasoningTokens { get; set; }
}

/// <summary>Maps <see cref="ModelRequest"/> to the chat completions request body.</summary>
internal static class OpenAiChatRequestBuilder
{
    public static string Build(string modelId, ModelRequest request)
    {
        var body = new OpenAiChatRequest
        {
            Model = modelId,
            Stream = true,
            StreamOptions = new OpenAiStreamOptions { IncludeUsage = true },
            Messages = request.Messages.Select(ToWire).ToList(),
            Tools = request.Tools.Count == 0 ? null : request.Tools.Select(ToWire).ToList(),
            MaxTokens = request.MaxOutputTokens,
            Temperature = request.Temperature,
        };
        return JsonSerializer.Serialize(body, OpenAiChatJsonContext.Default.OpenAiChatRequest);
    }

    private static OpenAiChatMessage ToWire(ModelMessage message) => message.Role switch
    {
        ModelRole.System => new OpenAiChatMessage { Role = "system", Content = message.Text },
        ModelRole.User => new OpenAiChatMessage { Role = "user", Content = message.Text },
        ModelRole.Assistant => ToAssistant(message),
        ModelRole.Tool => ToTool(message),
        _ => throw new ArgumentException($"Unknown message role '{message.Role}'."),
    };

    private static OpenAiChatMessage ToAssistant(ModelMessage message)
    {
        var calls = message.ToolCalls is { Count: > 0 } toolCalls
            ? toolCalls.Select(call => new OpenAiToolCall
            {
                Id = call.Id,
                Function = new OpenAiFunctionCall { Name = call.Name, Arguments = call.ArgumentsJson },
            }).ToList()
            : null;

        return new OpenAiChatMessage
        {
            Role = "assistant",
            Content = calls is not null && message.Text.Length == 0 ? null : message.Text,
            ToolCalls = calls,
        };
    }

    private static OpenAiChatMessage ToTool(ModelMessage message)
    {
        if (string.IsNullOrEmpty(message.ToolCallId))
        {
            throw new ArgumentException("A tool message must name the call it answers (ToolCallId).");
        }

        return new OpenAiChatMessage { Role = "tool", Content = message.Text, ToolCallId = message.ToolCallId };
    }

    private static OpenAiChatTool ToWire(ModelToolDefinition tool)
    {
        JsonElement parameters;
        try
        {
            using var document = JsonDocument.Parse(tool.ParametersJsonSchema);
            parameters = document.RootElement.Clone();
        }
        catch (JsonException ex)
        {
            throw new ArgumentException($"The parameters of tool '{tool.Name}' are not valid JSON.", ex);
        }

        return new OpenAiChatTool
        {
            Function = new OpenAiFunctionDefinition
            {
                Name = tool.Name,
                Description = tool.Description,
                Parameters = parameters,
            },
        };
    }
}
