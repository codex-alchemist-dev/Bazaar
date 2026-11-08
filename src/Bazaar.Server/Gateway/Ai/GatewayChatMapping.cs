using System.Text;
using System.Text.Json;
using Bazaar.Contracts.Gateway;
using Microsoft.Extensions.AI;

namespace Bazaar.Server.Gateway.Ai;

/// <summary>
/// Translates Microsoft.Extensions.AI shapes to and from the gateway contracts for <see cref="GatewayChatClient"/>.
/// Anything the gateway cannot carry is refused rather than dropped silently.
/// </summary>
internal static class GatewayChatMapping
{
    /// <summary>The <see cref="FunctionCallContent.AdditionalProperties"/> key that holds <see cref="ModelToolCall.ProviderData"/>.</summary>
    public const string ProviderDataKey = "bazaar.provider_data";

    /// <summary>
    /// Builds the gateway request. <see cref="ChatOptions.ModelId"/>, when set, replaces the route; policy is enforced by the gateway.
    /// </summary>
    public static ModelRequest ToModelRequest(string agentId, string route, IEnumerable<ChatMessage> messages, ChatOptions? options)
    {
        ArgumentNullException.ThrowIfNull(messages);

        var modelMessages = new List<ModelMessage>();
        if (!string.IsNullOrEmpty(options?.Instructions))
        {
            modelMessages.Add(new ModelMessage(ModelRole.System, options.Instructions));
        }

        foreach (var message in messages)
        {
            AddMessages(modelMessages, message);
        }

        var tools = options?.Tools is null ? [] : options.Tools.Select(ToToolDefinition).ToList();
        var requestedRoute = string.IsNullOrWhiteSpace(options?.ModelId) ? route : options.ModelId;

        return new ModelRequest(
            AgentId: agentId,
            Route: requestedRoute,
            Messages: modelMessages,
            Tools: tools,
            MaxOutputTokens: options?.MaxOutputTokens,
            Temperature: options?.Temperature);
    }

    /// <summary>Parses the arguments of one tool call. Empty arguments mean no parameters.</summary>
    public static Dictionary<string, object?> ParseArguments(string callId, string argumentsJson)
    {
        if (argumentsJson.Trim().Length == 0)
        {
            return [];
        }

        try
        {
            return JsonSerializer.Deserialize<Dictionary<string, object?>>(argumentsJson) ?? [];
        }
        catch (JsonException)
        {
            throw new GatewayModelException("malformed_tool_json", $"Tool call '{callId}' has arguments that are not valid JSON.");
        }
    }

    public static ChatFinishReason ToFinishReason(StopReason stopReason) => stopReason switch
    {
        StopReason.ToolUse => ChatFinishReason.ToolCalls,
        StopReason.MaxTokens => ChatFinishReason.Length,
        StopReason.ContentFilter => ChatFinishReason.ContentFilter,
        _ => ChatFinishReason.Stop,
    };

    /// <summary>
    /// Input tokens include cache reads and writes, as Microsoft.Extensions.AI expects. The gateway's <c>Input</c> excludes them.
    /// </summary>
    public static UsageDetails ToUsageDetails(GatewayTokenUsage usage)
    {
        var input = usage.Input + usage.CacheWrite5m + usage.CacheWrite1h + usage.CacheRead;
        var details = new UsageDetails
        {
            InputTokenCount = input,
            OutputTokenCount = usage.Output,
            TotalTokenCount = input + usage.Output,
            CachedInputTokenCount = usage.CacheRead,
            ReasoningTokenCount = usage.Thinking,
        };
        details.AdditionalCounts ??= [];
        details.AdditionalCounts["cache_write_5m"] = usage.CacheWrite5m;
        details.AdditionalCounts["cache_write_1h"] = usage.CacheWrite1h;
        return details;
    }

    private static void AddMessages(List<ModelMessage> output, ChatMessage message)
    {
        var role = ToRole(message.Role);
        var text = new StringBuilder();
        List<ModelToolCall>? toolCalls = null;
        var toolResults = new List<ModelMessage>();

        foreach (var content in message.Contents)
        {
            switch (content)
            {
                case TextContent textContent:
                    text.Append(textContent.Text);
                    break;

                case FunctionCallContent call:
                    (toolCalls ??= []).Add(new ModelToolCall(call.CallId, call.Name, SerializeArguments(call.Arguments), GetProviderData(call)));
                    break;

                case FunctionResultContent result:
                    toolResults.Add(new ModelMessage(ModelRole.Tool, ResultText(result), result.CallId));
                    break;

                case TextReasoningContent:
                    // Reasoning from an earlier turn is not sent back to the model.
                    break;

                default:
                    throw new NotSupportedException($"Content '{content.GetType().Name}' is not supported by the model gateway.");
            }
        }

        if (text.Length > 0 || toolCalls is not null || toolResults.Count == 0)
        {
            output.Add(new ModelMessage(role, text.ToString(), null, toolCalls));
        }

        output.AddRange(toolResults);
    }

    private static ModelRole ToRole(ChatRole role)
    {
        if (role == ChatRole.System)
        {
            return ModelRole.System;
        }

        if (role == ChatRole.User)
        {
            return ModelRole.User;
        }

        if (role == ChatRole.Assistant)
        {
            return ModelRole.Assistant;
        }

        if (role == ChatRole.Tool)
        {
            return ModelRole.Tool;
        }

        throw new NotSupportedException($"Chat role '{role.Value}' has no gateway equivalent.");
    }

    private static ModelToolDefinition ToToolDefinition(AITool tool)
    {
        if (tool is not AIFunctionDeclaration function)
        {
            throw new NotSupportedException($"Tool '{tool.Name}' is not a function, and the gateway supports function tools only.");
        }

        return new ModelToolDefinition(function.Name, function.Description, function.JsonSchema.GetRawText());
    }

    /// <summary>
    /// Carries the opaque provider token (for example Gemini's thought signature) on the call's additional properties,
    /// so it can be sent back unchanged on the next request. Null tokens are not stored.
    /// </summary>
    public static void SetProviderData(FunctionCallContent call, string? providerData)
    {
        if (providerData is null)
        {
            return;
        }

        call.AdditionalProperties ??= [];
        call.AdditionalProperties[ProviderDataKey] = providerData;
    }

    public static string? GetProviderData(FunctionCallContent call) =>
        call.AdditionalProperties is not null && call.AdditionalProperties.TryGetValue(ProviderDataKey, out var value) ? value as string : null;

    private static string SerializeArguments(IDictionary<string, object?>? arguments) =>
        arguments is null ? "{}" : JsonSerializer.Serialize(arguments);

    /// <summary>A string result is sent as it is; any other value is sent as JSON.</summary>
    private static string ResultText(FunctionResultContent result) => result.Result switch
    {
        null when result.Exception is { } exception => exception.Message,
        null => string.Empty,
        string text => text,
        JsonElement element => element.GetRawText(),
        var other => JsonSerializer.Serialize(other),
    };
}
