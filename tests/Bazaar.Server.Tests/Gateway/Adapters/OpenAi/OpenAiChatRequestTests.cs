using System.Text.Json;
using Bazaar.Contracts.Gateway;
using Bazaar.Server.Gateway.Adapters;
using static Bazaar.Server.Tests.Gateway.Adapters.OpenAi.OpenAiChatTestRig;

namespace Bazaar.Server.Tests.Gateway.Adapters.OpenAi;

public class OpenAiChatRequestTests
{
    private const string WeatherSchema = """{"type":"object","properties":{"city":{"type":"string"}},"required":["city"]}""";

    private static JsonElement SentBody(StubHandler handler)
    {
        var body = handler.Requests.Single().Body;
        return JsonDocument.Parse(body).RootElement.Clone();
    }

    private static StubHandler HandlerWithEmptyStream()
    {
        var handler = new StubHandler();
        handler.Reply(Replies.Sse("data: [DONE]\n\n"));
        return handler;
    }

    [Fact]
    public async Task Request_body_carries_messages_tools_limits_and_stream_options()
    {
        var request = new ModelRequest(
            "agent-1",
            "route-a",
            [
                new ModelMessage(ModelRole.System, "Be brief."),
                new ModelMessage(ModelRole.User, "Weather in Lisbon?"),
                new ModelMessage(ModelRole.Assistant, "Checking.", ToolCalls: [new ModelToolCall("call_1", "get_weather", "{\"city\":\"Lisbon\"}")]),
                new ModelMessage(ModelRole.Tool, "{\"tempC\":21}", ToolCallId: "call_1"),
            ],
            [new ModelToolDefinition("get_weather", "Get the weather.", WeatherSchema)],
            MaxOutputTokens: 256,
            Temperature: 0.2);
        var handler = HandlerWithEmptyStream();

        await Run(handler, request: request);

        var captured = handler.Requests.Single();
        Assert.Equal(new Uri("https://provider.example/v1/chat/completions"), captured.Url);
        Assert.Equal("Bearer test-placeholder-key", captured.Authorization);

        var body = SentBody(handler);
        Assert.Equal("requested-model", body.GetProperty("model").GetString());
        Assert.True(body.GetProperty("stream").GetBoolean());
        Assert.True(body.GetProperty("stream_options").GetProperty("include_usage").GetBoolean());
        Assert.Equal(256, body.GetProperty("max_tokens").GetInt32());
        Assert.Equal(0.2, body.GetProperty("temperature").GetDouble());

        var messages = body.GetProperty("messages").EnumerateArray().ToList();
        Assert.Equal(["system", "user", "assistant", "tool"], messages.Select(m => m.GetProperty("role").GetString()));
        Assert.Equal("Be brief.", messages[0].GetProperty("content").GetString());

        var assistant = messages[2];
        Assert.Equal("Checking.", assistant.GetProperty("content").GetString());
        var call = Assert.Single(assistant.GetProperty("tool_calls").EnumerateArray());
        Assert.Equal("call_1", call.GetProperty("id").GetString());
        Assert.Equal("function", call.GetProperty("type").GetString());
        Assert.Equal("get_weather", call.GetProperty("function").GetProperty("name").GetString());
        Assert.Equal("{\"city\":\"Lisbon\"}", call.GetProperty("function").GetProperty("arguments").GetString());

        Assert.Equal("call_1", messages[3].GetProperty("tool_call_id").GetString());
        Assert.Equal("{\"tempC\":21}", messages[3].GetProperty("content").GetString());

        var tool = Assert.Single(body.GetProperty("tools").EnumerateArray());
        Assert.Equal("function", tool.GetProperty("type").GetString());
        Assert.Equal("get_weather", tool.GetProperty("function").GetProperty("name").GetString());
        Assert.Equal("Get the weather.", tool.GetProperty("function").GetProperty("description").GetString());
        Assert.Equal("object", tool.GetProperty("function").GetProperty("parameters").GetProperty("type").GetString());
    }

    [Fact]
    public async Task Assistant_tool_call_without_text_omits_content()
    {
        var request = new ModelRequest(
            "agent-1",
            "route-a",
            [
                new ModelMessage(ModelRole.User, "Weather?"),
                new ModelMessage(ModelRole.Assistant, "", ToolCalls: [new ModelToolCall("call_9", "get_weather", "{}")]),
                new ModelMessage(ModelRole.Tool, "{}", ToolCallId: "call_9"),
            ],
            []);
        var handler = HandlerWithEmptyStream();

        await Run(handler, request: request);

        var assistant = SentBody(handler).GetProperty("messages")[1];
        Assert.False(assistant.TryGetProperty("content", out _));
        Assert.Equal("call_9", assistant.GetProperty("tool_calls")[0].GetProperty("id").GetString());
    }

    [Fact]
    public async Task Request_without_tools_or_limits_omits_those_fields()
    {
        var handler = HandlerWithEmptyStream();

        await Run(handler);

        var body = SentBody(handler);
        Assert.False(body.TryGetProperty("tools", out _));
        Assert.False(body.TryGetProperty("max_tokens", out _));
        Assert.False(body.TryGetProperty("temperature", out _));
    }

    [Fact]
    public async Task Extra_headers_are_sent_and_no_key_means_no_authorization()
    {
        var handler = HandlerWithEmptyStream();
        var endpoint = new ProviderEndpoint(
            "https://provider.example/v1/",
            "requested-model",
            ApiKey: null,
            ExtraHeaders: new Dictionary<string, string> { ["X-Title"] = "Bazaar" });

        await Run(handler, endpoint: endpoint);

        var captured = handler.Requests.Single();
        Assert.Null(captured.Authorization);
        Assert.Equal("Bazaar", captured.Headers["X-Title"]);
        Assert.Equal(new Uri("https://provider.example/v1/chat/completions"), captured.Url);
    }

    [Fact]
    public async Task Tool_message_without_a_call_id_is_rejected_before_any_request()
    {
        var handler = new StubHandler();
        var request = new ModelRequest("agent-1", "route-a", [new ModelMessage(ModelRole.Tool, "{}")], []);

        await Assert.ThrowsAsync<ArgumentException>(() => Run(handler, request: request));

        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task Tool_schema_that_is_not_json_is_rejected_before_any_request()
    {
        var handler = new StubHandler();
        var request = new ModelRequest(
            "agent-1",
            "route-a",
            [new ModelMessage(ModelRole.User, "Hi")],
            [new ModelToolDefinition("broken", "Bad schema.", "{not json")]);

        await Assert.ThrowsAsync<ArgumentException>(() => Run(handler, request: request));

        Assert.Empty(handler.Requests);
    }
}
