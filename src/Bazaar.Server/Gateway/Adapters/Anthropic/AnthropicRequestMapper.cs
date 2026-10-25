using System.Text.Json;
using System.Text.Json.Nodes;
using Bazaar.Contracts.Gateway;

namespace Bazaar.Server.Gateway.Adapters.Anthropic;

/// <summary>Builds the JSON body of <c>POST /v1/messages</c> from a <see cref="ModelRequest"/>.</summary>
internal static class AnthropicRequestMapper
{
    /// <summary>Sent when the caller sets no output limit, because the Messages API requires <c>max_tokens</c>.</summary>
    public const int DefaultMaxTokens = 4096;

    /// <summary>
    /// Maps the request. System messages are hoisted into the top-level <c>system</c> field. Consecutive user-side turns
    /// (user text and tool results) merge into one message, as the API expects tool results in the next user turn.
    /// Throws <see cref="ArgumentException"/> for caller data that cannot be sent (a bad tool argument or schema); this is
    /// a caller bug, not an upstream failure.
    /// </summary>
    public static JsonObject Build(ModelRequest request, string modelId)
    {
        var body = new JsonObject
        {
            ["model"] = modelId,
            ["max_tokens"] = request.MaxOutputTokens ?? DefaultMaxTokens,
            ["stream"] = true,
        };

        if (request.Temperature is { } temperature)
        {
            body["temperature"] = temperature;
        }

        var system = request.Messages
            .Where(m => m.Role == ModelRole.System && m.Text.Length > 0)
            .Select(m => m.Text)
            .ToArray();
        if (system.Length > 0)
        {
            body["system"] = string.Join("\n\n", system);
        }

        var turns = new List<(string Role, List<JsonNode> Content)>();
        foreach (var message in request.Messages)
        {
            switch (message.Role)
            {
                case ModelRole.User:
                    AddTurn(turns, "user", TextBlocks(message.Text));
                    break;

                case ModelRole.Assistant:
                    var assistant = TextBlocks(message.Text);
                    foreach (var call in message.ToolCalls ?? [])
                    {
                        assistant.Add(ToolUseBlock(call));
                    }

                    AddTurn(turns, "assistant", assistant);
                    break;

                case ModelRole.Tool:
                    if (string.IsNullOrEmpty(message.ToolCallId))
                    {
                        throw new ArgumentException("A tool message must name the tool call it answers.", nameof(request));
                    }

                    AddTurn(turns, "user", [new JsonObject
                    {
                        ["type"] = "tool_result",
                        ["tool_use_id"] = message.ToolCallId,
                        ["content"] = message.Text,
                    }]);
                    break;

                default:
                    // System messages were hoisted above.
                    break;
            }
        }

        var messages = new JsonArray();
        foreach (var (role, content) in turns)
        {
            messages.Add(new JsonObject
            {
                ["role"] = role,
                ["content"] = new JsonArray(content.ToArray()),
            });
        }

        body["messages"] = messages;

        if (request.Tools.Count > 0)
        {
            var tools = new JsonArray();
            foreach (var tool in request.Tools)
            {
                tools.Add(new JsonObject
                {
                    ["name"] = tool.Name,
                    ["description"] = tool.Description,
                    ["input_schema"] = ParseSchema(tool),
                });
            }

            body["tools"] = tools;
        }

        return body;
    }

    private static List<JsonNode> TextBlocks(string text)
    {
        var blocks = new List<JsonNode>();
        if (text.Length > 0)
        {
            blocks.Add(new JsonObject { ["type"] = "text", ["text"] = text });
        }

        return blocks;
    }

    private static void AddTurn(List<(string Role, List<JsonNode> Content)> turns, string role, List<JsonNode> blocks)
    {
        if (blocks.Count == 0)
        {
            return;
        }

        if (turns.Count > 0 && turns[^1].Role == role)
        {
            turns[^1].Content.AddRange(blocks);
        }
        else
        {
            turns.Add((role, blocks));
        }
    }

    private static JsonObject ToolUseBlock(ModelToolCall call) => new()
    {
        ["type"] = "tool_use",
        ["id"] = call.Id,
        ["name"] = call.Name,
        ["input"] = ParseArguments(call),
    };

    private static JsonNode ParseArguments(ModelToolCall call)
    {
        if (string.IsNullOrWhiteSpace(call.ArgumentsJson))
        {
            return new JsonObject();
        }

        JsonNode? node;
        try
        {
            node = JsonNode.Parse(call.ArgumentsJson);
        }
        catch (JsonException ex)
        {
            throw new ArgumentException($"Arguments of tool call '{call.Id}' are not valid JSON.", nameof(call), ex);
        }

        return node as JsonObject ?? throw new ArgumentException($"Arguments of tool call '{call.Id}' must be a JSON object.", nameof(call));
    }

    private static JsonNode ParseSchema(ModelToolDefinition tool)
    {
        JsonNode? node;
        try
        {
            node = JsonNode.Parse(tool.ParametersJsonSchema);
        }
        catch (JsonException ex)
        {
            throw new ArgumentException($"The parameter schema of tool '{tool.Name}' is not valid JSON.", nameof(tool), ex);
        }

        return node as JsonObject ?? throw new ArgumentException($"The parameter schema of tool '{tool.Name}' must be a JSON object.", nameof(tool));
    }
}
