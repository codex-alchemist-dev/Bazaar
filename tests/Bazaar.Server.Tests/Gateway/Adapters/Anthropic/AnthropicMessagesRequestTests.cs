using System.Text.Json;
using Bazaar.Contracts.Gateway;
using Bazaar.Server.Gateway.Adapters;

namespace Bazaar.Server.Tests.Gateway.Adapters.Anthropic;

/// <summary>The request the adapter sends. Each test reads the JSON body and headers as the stub handler received them.</summary>
public class AnthropicMessagesRequestTests
{
    private static readonly string _schema = """{"type":"object","properties":{"city":{"type":"string"}},"required":["city"]}""";

    private static async Task<SentRequest> SendAndCapture(ModelRequest request, ProviderEndpoint? endpoint = null)
    {
        using var handler = new StubHandler(_ => AnthropicTestSupport.Sse(AnthropicTestSupport.Fixture("text_stream.sse")));
        var adapter = AnthropicTestSupport.Adapter(handler);
        await AnthropicTestSupport.Collect(adapter, endpoint ?? AnthropicTestSupport.Endpoint(), request, TestContext.Current.CancellationToken);
        return Assert.Single(handler.Sent);
    }

    [Fact]
    public async Task Posts_to_v1_messages_with_auth_version_and_extra_headers()
    {
        var extra = new Dictionary<string, string> { ["x-tenant"] = "bazaar-test" };

        var sent = await SendAndCapture(AnthropicTestSupport.SimpleRequest(), AnthropicTestSupport.Endpoint(extraHeaders: extra));

        Assert.Equal("https://api.example.test/v1/messages", sent.Url.ToString());
        Assert.Equal("test-key", sent.Headers["x-api-key"]);
        Assert.Equal("2023-06-01", sent.Headers["anthropic-version"]);
        Assert.Equal("bazaar-test", sent.Headers["x-tenant"]);
    }

    [Fact]
    public async Task Body_sets_model_stream_and_default_max_tokens()
    {
        var sent = await SendAndCapture(AnthropicTestSupport.SimpleRequest());

        using var body = JsonDocument.Parse(sent.Body);
        var root = body.RootElement;
        Assert.Equal("claude-haiku-5-5", root.GetProperty("model").GetString());
        Assert.True(root.GetProperty("stream").GetBoolean());
        Assert.Equal(4096, root.GetProperty("max_tokens").GetInt32());
        Assert.False(root.TryGetProperty("system", out _));
        Assert.False(root.TryGetProperty("tools", out _));
        Assert.False(root.TryGetProperty("temperature", out _));
    }

    [Fact]
    public async Task Explicit_max_output_and_temperature_are_sent()
    {
        var request = new ModelRequest("agent-1", "claude-haiku-5-5", [new ModelMessage(ModelRole.User, "Hi")], [], MaxOutputTokens: 512, Temperature: 0.2);

        var sent = await SendAndCapture(request);

        using var body = JsonDocument.Parse(sent.Body);
        Assert.Equal(512, body.RootElement.GetProperty("max_tokens").GetInt32());
        Assert.Equal(0.2, body.RootElement.GetProperty("temperature").GetDouble());
    }

    [Fact]
    public async Task System_messages_are_hoisted_into_the_top_level_system_field()
    {
        var request = new ModelRequest("agent-1", "claude-haiku-5-5",
            [
                new ModelMessage(ModelRole.System, "You are terse."),
                new ModelMessage(ModelRole.System, "Use tools when asked."),
                new ModelMessage(ModelRole.User, "Hi"),
            ],
            []);

        var sent = await SendAndCapture(request);

        using var body = JsonDocument.Parse(sent.Body);
        var root = body.RootElement;
        Assert.Equal("You are terse.\n\nUse tools when asked.", root.GetProperty("system").GetString());

        var messages = root.GetProperty("messages");
        Assert.Equal(1, messages.GetArrayLength());
        Assert.Equal("user", messages[0].GetProperty("role").GetString());
    }

    [Fact]
    public async Task Tool_round_trip_maps_tool_use_and_merges_tool_results_into_one_user_turn()
    {
        var request = new ModelRequest("agent-1", "claude-haiku-5-5",
            [
                new ModelMessage(ModelRole.User, "Weather in Paris and Rome?"),
                new ModelMessage(ModelRole.Assistant, "Checking both.",
                    ToolCalls:
                    [
                        new ModelToolCall("toolu_1", "get_weather", "{\"city\":\"Paris\"}"),
                        new ModelToolCall("toolu_2", "get_weather", "{\"city\":\"Rome\"}"),
                    ]),
                new ModelMessage(ModelRole.Tool, "{\"temp\":18}", ToolCallId: "toolu_1"),
                new ModelMessage(ModelRole.Tool, "{\"temp\":21}", ToolCallId: "toolu_2"),
            ],
            [new ModelToolDefinition("get_weather", "Current weather for a city.", _schema)]);

        var sent = await SendAndCapture(request);

        using var body = JsonDocument.Parse(sent.Body);
        var messages = body.RootElement.GetProperty("messages");
        Assert.Equal(3, messages.GetArrayLength());

        var user = messages[0];
        Assert.Equal("user", user.GetProperty("role").GetString());
        Assert.Equal("Weather in Paris and Rome?", user.GetProperty("content")[0].GetProperty("text").GetString());

        var assistant = messages[1];
        Assert.Equal("assistant", assistant.GetProperty("role").GetString());
        var assistantBlocks = assistant.GetProperty("content");
        Assert.Equal("text", assistantBlocks[0].GetProperty("type").GetString());
        Assert.Equal("Checking both.", assistantBlocks[0].GetProperty("text").GetString());
        Assert.Equal("tool_use", assistantBlocks[1].GetProperty("type").GetString());
        Assert.Equal("toolu_1", assistantBlocks[1].GetProperty("id").GetString());
        Assert.Equal("get_weather", assistantBlocks[1].GetProperty("name").GetString());
        Assert.Equal("Paris", assistantBlocks[1].GetProperty("input").GetProperty("city").GetString());
        Assert.Equal("Rome", assistantBlocks[2].GetProperty("input").GetProperty("city").GetString());

        // Both tool results sit in one user turn, after the assistant turn that made the calls.
        var results = messages[2];
        Assert.Equal("user", results.GetProperty("role").GetString());
        var resultBlocks = results.GetProperty("content");
        Assert.Equal(2, resultBlocks.GetArrayLength());
        Assert.Equal("tool_result", resultBlocks[0].GetProperty("type").GetString());
        Assert.Equal("toolu_1", resultBlocks[0].GetProperty("tool_use_id").GetString());
        Assert.Equal("{\"temp\":18}", resultBlocks[0].GetProperty("content").GetString());
        Assert.Equal("toolu_2", resultBlocks[1].GetProperty("tool_use_id").GetString());
    }

    [Fact]
    public async Task Tools_are_sent_with_name_description_and_input_schema_object()
    {
        var request = new ModelRequest("agent-1", "claude-haiku-5-5",
            [new ModelMessage(ModelRole.User, "Hi")],
            [new ModelToolDefinition("get_weather", "Current weather for a city.", _schema)]);

        var sent = await SendAndCapture(request);

        using var body = JsonDocument.Parse(sent.Body);
        var tool = body.RootElement.GetProperty("tools")[0];
        Assert.Equal("get_weather", tool.GetProperty("name").GetString());
        Assert.Equal("Current weather for a city.", tool.GetProperty("description").GetString());
        Assert.Equal("object", tool.GetProperty("input_schema").GetProperty("type").GetString());
        Assert.Equal("city", tool.GetProperty("input_schema").GetProperty("required")[0].GetString());
    }

    [Fact]
    public async Task Empty_tool_arguments_are_sent_as_an_empty_object()
    {
        var request = new ModelRequest("agent-1", "claude-haiku-5-5",
            [
                new ModelMessage(ModelRole.Assistant, "", ToolCalls: [new ModelToolCall("toolu_0", "list_items", "")]),
                new ModelMessage(ModelRole.Tool, "[]", ToolCallId: "toolu_0"),
            ],
            []);

        var sent = await SendAndCapture(request);

        using var body = JsonDocument.Parse(sent.Body);
        var block = body.RootElement.GetProperty("messages")[0].GetProperty("content")[0];
        Assert.Equal("tool_use", block.GetProperty("type").GetString());
        Assert.Equal(JsonValueKind.Object, block.GetProperty("input").ValueKind);
    }

    [Fact]
    public async Task Tool_message_without_a_call_id_is_a_caller_error()
    {
        var request = new ModelRequest("agent-1", "claude-haiku-5-5", [new ModelMessage(ModelRole.Tool, "result")], []);

        await Assert.ThrowsAsync<ArgumentException>(() => SendAndCapture(request));
    }

    [Fact]
    public async Task Tool_arguments_that_are_not_json_are_a_caller_error_and_nothing_is_sent()
    {
        var request = new ModelRequest("agent-1", "claude-haiku-5-5",
            [new ModelMessage(ModelRole.Assistant, "", ToolCalls: [new ModelToolCall("toolu_x", "get_weather", "{oops")])],
            []);
        using var handler = new StubHandler(_ => AnthropicTestSupport.Sse(AnthropicTestSupport.Fixture("text_stream.sse")));
        var adapter = AnthropicTestSupport.Adapter(handler);

        await Assert.ThrowsAsync<ArgumentException>(() => AnthropicTestSupport.Collect(adapter, AnthropicTestSupport.Endpoint(), request, TestContext.Current.CancellationToken));
        Assert.Empty(handler.Sent);
    }

    [Fact]
    public async Task Missing_api_key_sends_no_x_api_key_header()
    {
        var sent = await SendAndCapture(AnthropicTestSupport.SimpleRequest(), AnthropicTestSupport.Endpoint(apiKey: null));

        Assert.False(sent.Headers.ContainsKey("x-api-key"));
    }
}
