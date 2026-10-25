using System.Text.Json.Nodes;
using Bazaar.Contracts.Gateway;

namespace Bazaar.Server.Gateway.Adapters.Gemini;

/// <summary>
/// Maps a <see cref="ModelRequest"/> to a Gemini <c>GenerateContentRequest</c> body. System messages become
/// <c>systemInstruction</c>; consecutive turns with the same role are merged, so parallel tool results share one turn.
/// </summary>
internal static class GeminiRequestBuilder
{
    public static JsonObject Build(ModelRequest request)
    {
        var body = new JsonObject();

        var systemParts = request.Messages
            .Where(m => m.Role == ModelRole.System && m.Text.Length > 0)
            .Select(m => TextPart(m.Text))
            .ToArray();
        if (systemParts.Length > 0)
        {
            body["systemInstruction"] = new JsonObject { ["parts"] = new JsonArray(systemParts) };
        }

        body["contents"] = BuildContents(request.Messages);

        if (request.Tools.Count > 0)
        {
            var declarations = new JsonArray();
            foreach (var tool in request.Tools)
            {
                var declaration = new JsonObject
                {
                    ["name"] = tool.Name,
                    ["description"] = tool.Description,
                };
                if (TryParseObject(tool.ParametersJsonSchema) is { } schema)
                {
                    declaration["parameters"] = schema;
                }

                declarations.Add(declaration);
            }

            body["tools"] = new JsonArray(new JsonObject { ["functionDeclarations"] = declarations });
        }

        var generation = new JsonObject();
        if (request.MaxOutputTokens is { } maxOutputTokens)
        {
            generation["maxOutputTokens"] = maxOutputTokens;
        }

        if (request.Temperature is { } temperature)
        {
            generation["temperature"] = temperature;
        }

        if (generation.Count > 0)
        {
            body["generationConfig"] = generation;
        }

        return body;
    }

    private static JsonArray BuildContents(IReadOnlyList<ModelMessage> messages)
    {
        // A tool result carries the function name, which Gemini requires. It is looked up from the earlier assistant call.
        var names = new Dictionary<string, string>(StringComparer.Ordinal);
        var turns = new List<(string Role, List<JsonObject> Parts)>();

        foreach (var message in messages)
        {
            var parts = new List<JsonObject>();
            var role = "user";

            switch (message.Role)
            {
                case ModelRole.User:
                    if (message.Text.Length > 0)
                    {
                        parts.Add(TextPart(message.Text));
                    }

                    break;

                case ModelRole.Assistant:
                    role = "model";
                    if (message.Text.Length > 0)
                    {
                        parts.Add(TextPart(message.Text));
                    }

                    foreach (var call in message.ToolCalls ?? [])
                    {
                        names[call.Id] = call.Name;
                        parts.Add(new JsonObject
                        {
                            ["functionCall"] = new JsonObject
                            {
                                ["name"] = call.Name,
                                ["args"] = TryParseObject(call.ArgumentsJson) ?? new JsonObject(),
                            },
                        });
                    }

                    break;

                case ModelRole.Tool:
                    var name = message.ToolCallId is { } id && names.TryGetValue(id, out var known)
                        ? known
                        : message.ToolCallId ?? "unknown";
                    parts.Add(new JsonObject
                    {
                        ["functionResponse"] = new JsonObject
                        {
                            ["name"] = name,
                            ["response"] = TryParseObject(message.Text) ?? new JsonObject { ["result"] = message.Text },
                        },
                    });
                    break;

                default:
                    continue;
            }

            if (parts.Count == 0)
            {
                continue;
            }

            if (turns.Count > 0 && turns[^1].Role == role)
            {
                turns[^1].Parts.AddRange(parts);
            }
            else
            {
                turns.Add((role, parts));
            }
        }

        return new JsonArray(turns
            .Select(t => new JsonObject
            {
                ["role"] = t.Role,
                ["parts"] = new JsonArray(t.Parts.ToArray()),
            })
            .ToArray());
    }

    private static JsonObject TextPart(string text) => new() { ["text"] = text };

    /// <summary>Parses JSON text that must be an object. Returns null for invalid JSON or any other JSON kind.</summary>
    private static JsonObject? TryParseObject(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        try
        {
            return JsonNode.Parse(text) as JsonObject;
        }
        catch (System.Text.Json.JsonException)
        {
            return null;
        }
    }
}
