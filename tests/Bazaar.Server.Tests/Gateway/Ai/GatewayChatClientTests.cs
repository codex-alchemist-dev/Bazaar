using System.Text.Json;
using Bazaar.Contracts.Gateway;
using Bazaar.Server.Gateway;
using Bazaar.Server.Gateway.Ai;
using Bazaar.Server.Gateway.Fakes;
using Microsoft.Extensions.AI;

namespace Bazaar.Server.Tests.Gateway.Ai;

public class GatewayChatClientTests
{
    private const string _route = "custom/charles";

    /// <summary>Where the adapter keeps a tool call's provider token on the function call.</summary>
    private const string _providerDataKey = "bazaar.provider_data";

    private static readonly ChatMessage[] _hello = [new ChatMessage(ChatRole.User, "hello")];

    private static (GatewayChatClient Client, RecordingGateway Gateway, FakeProvider Provider) Create()
    {
        var provider = new FakeProvider();
        var gateway = new RecordingGateway(provider);
        return (new GatewayChatClient(gateway, "agent-1", _route), gateway, provider);
    }

    private static async Task<List<ChatResponseUpdate>> Stream(IAsyncEnumerable<ChatResponseUpdate> stream, List<ChatResponseUpdate>? into = null)
    {
        var updates = into ?? [];
        await foreach (var update in stream)
        {
            updates.Add(update);
        }

        return updates;
    }

    [Fact]
    public async Task Text_deltas_stream_as_text_and_done_becomes_the_final_update()
    {
        var (client, _, provider) = Create();
        provider.EnqueueCall(
            new ScriptStep.Emit(new ModelEvent.TextDelta("Hel")),
            new ScriptStep.Emit(new ModelEvent.TextDelta("lo")),
            new ScriptStep.Emit(new ModelEvent.Done(StopReason.EndTurn, "served-model")));

        var updates = await Stream(client.GetStreamingResponseAsync(_hello, cancellationToken: TestContext.Current.CancellationToken));

        Assert.Equal(3, updates.Count);
        Assert.Equal("Hel", updates[0].Text);
        Assert.Equal("lo", updates[1].Text);
        var final = updates[2];
        Assert.Empty(final.Contents);
        Assert.Equal(ChatFinishReason.Stop, final.FinishReason);
        Assert.Equal("served-model", final.ModelId);
        Assert.Equal(ChatRole.Assistant, final.Role);
    }

    [Fact]
    public async Task Thinking_deltas_stream_as_reasoning_content()
    {
        var (client, _, provider) = Create();
        provider.EnqueueCall(
            new ScriptStep.Emit(new ModelEvent.ThinkingDelta("let me think")),
            new ScriptStep.Emit(new ModelEvent.Done(StopReason.EndTurn, "m")));

        var updates = await Stream(client.GetStreamingResponseAsync(_hello, cancellationToken: TestContext.Current.CancellationToken));

        var reasoning = Assert.IsType<TextReasoningContent>(Assert.Single(updates[0].Contents));
        Assert.Equal("let me think", reasoning.Text);
    }

    [Fact]
    public async Task Tool_call_arguments_split_across_deltas_become_one_function_call()
    {
        var (client, _, provider) = Create();
        provider.EnqueueCall(
            new ScriptStep.Emit(new ModelEvent.ToolCallStart("call-1", "get_weather")),
            new ScriptStep.Emit(new ModelEvent.ToolCallArgsDelta("call-1", "{\"city\":")),
            new ScriptStep.Emit(new ModelEvent.ToolCallArgsDelta("call-1", "\"Oslo\",\"days\":2}")),
            new ScriptStep.Emit(new ModelEvent.ToolCallEnd("call-1")),
            new ScriptStep.Emit(new ModelEvent.Done(StopReason.ToolUse, "m")));

        var updates = await Stream(client.GetStreamingResponseAsync(_hello, cancellationToken: TestContext.Current.CancellationToken));

        var call = Assert.IsType<FunctionCallContent>(Assert.Single(updates.SelectMany(u => u.Contents)));
        Assert.Equal("call-1", call.CallId);
        Assert.Equal("get_weather", call.Name);
        Assert.Equal("Oslo", ((JsonElement)call.Arguments!["city"]!).GetString());
        Assert.Equal(2, ((JsonElement)call.Arguments["days"]!).GetInt32());
        Assert.Equal(ChatFinishReason.ToolCalls, updates[^1].FinishReason);
    }

    [Fact]
    public async Task Interleaved_tool_calls_are_kept_apart_by_call_id()
    {
        var (client, _, provider) = Create();
        provider.EnqueueCall(
            new ScriptStep.Emit(new ModelEvent.ToolCallStart("a", "first")),
            new ScriptStep.Emit(new ModelEvent.ToolCallStart("b", "second")),
            new ScriptStep.Emit(new ModelEvent.ToolCallArgsDelta("b", "{\"n\":2}")),
            new ScriptStep.Emit(new ModelEvent.ToolCallArgsDelta("a", "{\"n\":1}")),
            new ScriptStep.Emit(new ModelEvent.ToolCallEnd("b")),
            new ScriptStep.Emit(new ModelEvent.ToolCallEnd("a")),
            new ScriptStep.Emit(new ModelEvent.Done(StopReason.ToolUse, "m")));

        var calls = (await Stream(client.GetStreamingResponseAsync(_hello, cancellationToken: TestContext.Current.CancellationToken)))
            .SelectMany(u => u.Contents).OfType<FunctionCallContent>().ToList();

        Assert.Equal(["b", "a"], calls.Select(c => c.CallId));
        Assert.Equal(["second", "first"], calls.Select(c => c.Name));
        Assert.Equal(2, ((JsonElement)calls[0].Arguments!["n"]!).GetInt32());
        Assert.Equal(1, ((JsonElement)calls[1].Arguments!["n"]!).GetInt32());
    }

    [Fact]
    public async Task Provider_data_from_the_tool_call_start_is_carried_on_the_function_call()
    {
        var (client, _, provider) = Create();
        provider.EnqueueCall(
            new ScriptStep.Emit(new ModelEvent.ToolCallStart("call_0", "get_weather", ProviderData: "c2lnLWFiYw==")),
            new ScriptStep.Emit(new ModelEvent.ToolCallArgsDelta("call_0", "{\"city\":\"Paris\"}")),
            new ScriptStep.Emit(new ModelEvent.ToolCallEnd("call_0")),
            new ScriptStep.Emit(new ModelEvent.Done(StopReason.ToolUse, "m")));

        var updates = await Stream(client.GetStreamingResponseAsync(_hello, cancellationToken: TestContext.Current.CancellationToken));

        var call = Assert.IsType<FunctionCallContent>(Assert.Single(updates.SelectMany(u => u.Contents)));
        Assert.Equal("c2lnLWFiYw==", call.AdditionalProperties![_providerDataKey]);
    }

    [Fact]
    public async Task Provider_data_round_trips_from_a_streamed_tool_call_into_the_next_request()
    {
        var (client, gateway, provider) = Create();
        provider.EnqueueCall(
            new ScriptStep.Emit(new ModelEvent.ToolCallStart("call_0", "get_weather", ProviderData: "c2lnLWFiYw==")),
            new ScriptStep.Emit(new ModelEvent.ToolCallArgsDelta("call_0", "{\"city\":\"Paris\"}")),
            new ScriptStep.Emit(new ModelEvent.ToolCallEnd("call_0")),
            new ScriptStep.Emit(new ModelEvent.Done(StopReason.ToolUse, "m")));
        provider.EnqueueCall(new ScriptStep.Emit(new ModelEvent.Done(StopReason.EndTurn, "m")));
        var token = TestContext.Current.CancellationToken;

        var first = await Stream(client.GetStreamingResponseAsync(_hello, cancellationToken: token));
        var call = Assert.IsType<FunctionCallContent>(Assert.Single(first.SelectMany(u => u.Contents)));
        var history = new List<ChatMessage>
        {
            new(ChatRole.User, "hello"),
            new(ChatRole.Assistant, [call]),
            new(ChatRole.Tool, [new FunctionResultContent("call_0", "sunny")]),
        };
        await Stream(client.GetStreamingResponseAsync(history, cancellationToken: token));

        var sent = Assert.Single(gateway.Requests.Skip(1));
        var assistant = Assert.Single(sent.Messages, m => m.ToolCalls is not null);
        var roundTripped = Assert.Single(assistant.ToolCalls!);
        Assert.Equal("c2lnLWFiYw==", roundTripped.ProviderData);
        Assert.Equal("call_0", roundTripped.Id);
        Assert.Equal("get_weather", roundTripped.Name);
    }

    [Fact]
    public async Task Tool_call_without_provider_data_sends_no_provider_data_back()
    {
        var (client, gateway, provider) = Create();
        provider.EnqueueCall(
            new ScriptStep.Emit(new ModelEvent.ToolCallStart("call-1", "get_weather")),
            new ScriptStep.Emit(new ModelEvent.ToolCallEnd("call-1")),
            new ScriptStep.Emit(new ModelEvent.Done(StopReason.ToolUse, "m")));
        provider.EnqueueCall(new ScriptStep.Emit(new ModelEvent.Done(StopReason.EndTurn, "m")));
        var token = TestContext.Current.CancellationToken;

        var first = await Stream(client.GetStreamingResponseAsync(_hello, cancellationToken: token));
        var call = Assert.IsType<FunctionCallContent>(Assert.Single(first.SelectMany(u => u.Contents)));
        Assert.False(call.AdditionalProperties?.ContainsKey(_providerDataKey) ?? false);

        await Stream(client.GetStreamingResponseAsync([new ChatMessage(ChatRole.Assistant, [call])], cancellationToken: token));

        var roundTripped = Assert.Single(Assert.Single(gateway.Requests.Skip(1)).Messages.Single().ToolCalls!);
        Assert.Null(roundTripped.ProviderData);
    }

    [Fact]
    public async Task Tool_call_with_no_arguments_gets_an_empty_dictionary()
    {
        var (client, _, provider) = Create();
        provider.EnqueueCall(
            new ScriptStep.Emit(new ModelEvent.ToolCallStart("c", "ping")),
            new ScriptStep.Emit(new ModelEvent.ToolCallEnd("c")),
            new ScriptStep.Emit(new ModelEvent.Done(StopReason.ToolUse, "m")));

        var updates = await Stream(client.GetStreamingResponseAsync(_hello, cancellationToken: TestContext.Current.CancellationToken));

        var call = Assert.IsType<FunctionCallContent>(Assert.Single(updates.SelectMany(u => u.Contents)));
        Assert.Empty(call.Arguments!);
    }

    [Fact]
    public async Task Usage_maps_to_usage_content_with_cache_counts()
    {
        var (client, _, provider) = Create();
        provider.EnqueueCall(
            new ScriptStep.Emit(new ModelEvent.TextDelta("x")),
            new ScriptStep.Emit(new ModelEvent.UsageReported(new GatewayTokenUsage(Input: 10, CacheWrite5m: 2, CacheWrite1h: 3, CacheRead: 5, Output: 7, Thinking: 4))),
            new ScriptStep.Emit(new ModelEvent.Done(StopReason.EndTurn, "m")));

        var updates = await Stream(client.GetStreamingResponseAsync(_hello, cancellationToken: TestContext.Current.CancellationToken));

        var usage = Assert.IsType<UsageContent>(updates[1].Contents.Single());
        Assert.Equal(20, usage.Details.InputTokenCount);
        Assert.Equal(7, usage.Details.OutputTokenCount);
        Assert.Equal(27, usage.Details.TotalTokenCount);
        Assert.Equal(5, usage.Details.CachedInputTokenCount);
        Assert.Equal(4, usage.Details.ReasoningTokenCount);
        Assert.Equal(2, usage.Details.AdditionalCounts!["cache_write_5m"]);
        Assert.Equal(3, usage.Details.AdditionalCounts["cache_write_1h"]);
        Assert.Equal(ChatFinishReason.Stop, updates[^1].FinishReason);
    }

    [Theory]
    [InlineData(StopReason.EndTurn, "stop")]
    [InlineData(StopReason.ToolUse, "tool_calls")]
    [InlineData(StopReason.MaxTokens, "length")]
    [InlineData(StopReason.ContentFilter, "content_filter")]
    [InlineData(StopReason.Other, "stop")]
    public async Task Stop_reasons_map_to_finish_reasons(StopReason stopReason, string expectedFinishReason)
    {
        var (client, _, provider) = Create();
        provider.EnqueueCall(new ScriptStep.Emit(new ModelEvent.Done(stopReason, "m")));

        var updates = await Stream(client.GetStreamingResponseAsync(_hello, cancellationToken: TestContext.Current.CancellationToken));

        Assert.Equal(expectedFinishReason, updates[^1].FinishReason?.Value);
    }

    [Fact]
    public async Task Warning_does_not_change_the_response()
    {
        var (client, _, provider) = Create();
        provider.EnqueueCall(
            new ScriptStep.Emit(new ModelEvent.Warning("long_prompt", "The prompt is long.")),
            new ScriptStep.Emit(new ModelEvent.TextDelta("ok")),
            new ScriptStep.Emit(new ModelEvent.Done(StopReason.EndTurn, "m")));

        var updates = await Stream(client.GetStreamingResponseAsync(_hello, cancellationToken: TestContext.Current.CancellationToken));

        Assert.Equal("ok", string.Concat(updates.Select(u => u.Text)));
        Assert.Equal(2, updates.Count);
    }

    [Fact]
    public async Task Failure_throws_gateway_model_exception_after_earlier_updates()
    {
        var (client, _, provider) = Create();
        provider.EnqueueCall(
            new ScriptStep.Emit(new ModelEvent.TextDelta("partial")),
            new ScriptStep.RateLimited(RetryAfterSeconds: 30));
        var updates = new List<ChatResponseUpdate>();

        var error = await Assert.ThrowsAsync<GatewayModelException>(() => Stream(client.GetStreamingResponseAsync(_hello, cancellationToken: TestContext.Current.CancellationToken), updates));

        Assert.Equal("rate_limited", error.Code);
        Assert.Equal(30, error.RetryAfterSeconds);
        Assert.Equal("partial", string.Concat(updates.Select(u => u.Text)));
    }

    [Fact]
    public async Task Spend_cap_and_dropped_stream_throw_with_their_codes()
    {
        var (client, _, provider) = Create();
        provider.EnqueueCall(new ScriptStep.SpendCapped());
        provider.EnqueueCall(new ScriptStep.ConnectionDropped());

        var capped = await Assert.ThrowsAsync<GatewayModelException>(() => Stream(client.GetStreamingResponseAsync(_hello, cancellationToken: TestContext.Current.CancellationToken)));
        var dropped = await Assert.ThrowsAsync<GatewayModelException>(() => Stream(client.GetStreamingResponseAsync(_hello, cancellationToken: TestContext.Current.CancellationToken)));

        Assert.Equal("spend_capped", capped.Code);
        Assert.Null(capped.RetryAfterSeconds);
        Assert.Equal("stream_dropped", dropped.Code);
    }

    [Fact]
    public async Task Malformed_tool_json_throws_malformed_tool_json_and_yields_no_call()
    {
        var (client, _, provider) = Create();
        provider.EnqueueCall(new ScriptStep.MalformedToolJson("call-1", "get_weather", "{\"city\":"));
        var updates = new List<ChatResponseUpdate>();

        var error = await Assert.ThrowsAsync<GatewayModelException>(() => Stream(client.GetStreamingResponseAsync(_hello, cancellationToken: TestContext.Current.CancellationToken), updates));

        Assert.Equal("malformed_tool_json", error.Code);
        Assert.DoesNotContain(updates.SelectMany(u => u.Contents), c => c is FunctionCallContent);
    }

    [Fact]
    public async Task Stream_that_ends_without_done_throws_stream_dropped()
    {
        var (client, _, provider) = Create();
        provider.EnqueueCall(new ScriptStep.Emit(new ModelEvent.TextDelta("unfinished")));

        var error = await Assert.ThrowsAsync<GatewayModelException>(() => Stream(client.GetStreamingResponseAsync(_hello, cancellationToken: TestContext.Current.CancellationToken)));

        Assert.Equal("stream_dropped", error.Code);
    }

    [Fact]
    public async Task Request_maps_system_user_assistant_and_tool_messages()
    {
        var (client, gateway, provider) = Create();
        provider.EnqueueCall(new ScriptStep.Emit(new ModelEvent.Done(StopReason.EndTurn, "m")));
        var arguments = new Dictionary<string, object?> { ["q"] = "x" };
        ChatMessage[] messages =
        [
            new ChatMessage(ChatRole.System, "be brief"),
            new ChatMessage(ChatRole.User, "look it up"),
            new ChatMessage(ChatRole.Assistant, [new TextContent("ok"), new FunctionCallContent("call-1", "lookup", arguments)]),
            new ChatMessage(ChatRole.Tool, [new FunctionResultContent("call-1", "found it")]),
        ];

        await Stream(client.GetStreamingResponseAsync(messages, cancellationToken: TestContext.Current.CancellationToken));

        var request = Assert.Single(gateway.Requests);
        string[] expected =
        [
            "System|be brief||",
            "User|look it up||",
            "Assistant|ok||call-1:lookup:{\"q\":\"x\"}",
            "Tool|found it|call-1|",
        ];
        Assert.Equal(expected, request.Messages.Select(Describe));
    }

    /// <summary>Flattens a message as Role|Text|ToolCallId|ToolCalls, because the record's tool-call list compares by reference.</summary>
    private static string Describe(ModelMessage message) =>
        string.Join(
            "|",
            message.Role,
            message.Text,
            message.ToolCallId,
            string.Join(",", (message.ToolCalls ?? []).Select(c => $"{c.Id}:{c.Name}:{c.ArgumentsJson}")));

    [Fact]
    public async Task Tool_result_objects_are_sent_as_json_and_instructions_lead_the_messages()
    {
        var (client, gateway, provider) = Create();
        provider.EnqueueCall(new ScriptStep.Emit(new ModelEvent.Done(StopReason.EndTurn, "m")));
        ChatMessage[] messages =
        [
            new ChatMessage(ChatRole.Tool, [new FunctionResultContent("call-2", new { temp = 5 })]),
        ];

        await Stream(client.GetStreamingResponseAsync(messages, new ChatOptions { Instructions = "Answer in one word." }, TestContext.Current.CancellationToken));

        var request = Assert.Single(gateway.Requests);
        string[] expected = ["System|Answer in one word.||", "Tool|{\"temp\":5}|call-2|"];
        Assert.Equal(expected, request.Messages.Select(Describe));
    }

    [Fact]
    public async Task Tool_definitions_carry_name_description_and_schema_text()
    {
        var (client, gateway, provider) = Create();
        provider.EnqueueCall(new ScriptStep.Emit(new ModelEvent.Done(StopReason.EndTurn, "m")));
        var weather = AIFunctionFactory.Create((string city) => city, "get_weather", "Gets the weather for a city.");
        var options = new ChatOptions { Tools = [weather] };

        await Stream(client.GetStreamingResponseAsync(_hello, options, TestContext.Current.CancellationToken));

        var request = Assert.Single(gateway.Requests);
        var tool = Assert.Single(request.Tools);
        Assert.Equal("get_weather", tool.Name);
        Assert.Equal("Gets the weather for a city.", tool.Description);
        using var schema = JsonDocument.Parse(tool.ParametersJsonSchema);
        Assert.True(schema.RootElement.GetProperty("properties").TryGetProperty("city", out _));
    }

    [Fact]
    public async Task Max_tokens_and_temperature_are_carried_to_the_request()
    {
        var (client, gateway, provider) = Create();
        provider.EnqueueCall(new ScriptStep.Emit(new ModelEvent.Done(StopReason.EndTurn, "m")));

        await Stream(client.GetStreamingResponseAsync(_hello, new ChatOptions { MaxOutputTokens = 256, Temperature = 0.2f }, TestContext.Current.CancellationToken));

        var request = Assert.Single(gateway.Requests);
        Assert.Equal(256, request.MaxOutputTokens);
        Assert.Equal(0.2, request.Temperature!.Value, precision: 5);
        Assert.Equal("agent-1", request.AgentId);
        Assert.Equal(_route, request.Route);
    }

    [Fact]
    public async Task Model_id_overrides_the_route_for_one_call_only()
    {
        var (client, gateway, provider) = Create();
        provider.EnqueueCall(new ScriptStep.Emit(new ModelEvent.Done(StopReason.EndTurn, "m")));
        provider.EnqueueCall(new ScriptStep.Emit(new ModelEvent.Done(StopReason.EndTurn, "m")));

        await Stream(client.GetStreamingResponseAsync(_hello, new ChatOptions { ModelId = "anthropic-direct:claude-haiku-5-5" }, TestContext.Current.CancellationToken));
        await Stream(client.GetStreamingResponseAsync(_hello, new ChatOptions(), TestContext.Current.CancellationToken));

        Assert.Equal(["anthropic-direct:claude-haiku-5-5", _route], gateway.Requests.Select(r => r.Route));
    }

    [Fact]
    public async Task Non_streaming_response_aggregates_the_streamed_text()
    {
        var (client, _, provider) = Create();
        provider.EnqueueCall(
            new ScriptStep.Emit(new ModelEvent.TextDelta("_hello")),
            new ScriptStep.Emit(new ModelEvent.TextDelta(" world")),
            new ScriptStep.Emit(new ModelEvent.UsageReported(new GatewayTokenUsage(Input: 3, CacheWrite5m: 0, CacheWrite1h: 0, CacheRead: 0, Output: 2, Thinking: 0))),
            new ScriptStep.Emit(new ModelEvent.Done(StopReason.MaxTokens, "served-model")));

        var response = await client.GetResponseAsync(_hello, cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal("_hello world", response.Text);
        Assert.Equal(ChatFinishReason.Length, response.FinishReason);
        Assert.Equal("served-model", response.ModelId);
        Assert.Equal(5, response.Usage!.TotalTokenCount);
    }

    [Fact]
    public async Task Non_streaming_response_surfaces_a_gateway_failure()
    {
        var (client, _, provider) = Create();
        provider.EnqueueCall(new ScriptStep.RateLimited());

        var error = await Assert.ThrowsAsync<GatewayModelException>(() => client.GetResponseAsync(_hello, cancellationToken: TestContext.Current.CancellationToken));

        Assert.Equal("rate_limited", error.Code);
    }

    [Fact]
    public async Task Cancellation_token_reaches_the_gateway_and_cancels_the_stream()
    {
        var (client, gateway, provider) = Create();
        provider.EnqueueCall(new ScriptStep.Emit(new ModelEvent.Done(StopReason.EndTurn, "m")));
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Stream(client.GetStreamingResponseAsync(_hello, cancellationToken: cts.Token)));

        Assert.Equal(cts.Token, Assert.Single(gateway.Tokens));
    }

    [Fact]
    public async Task Cancellation_token_is_passed_through_on_a_live_call()
    {
        var (client, gateway, provider) = Create();
        provider.EnqueueCall(new ScriptStep.Emit(new ModelEvent.Done(StopReason.EndTurn, "m")));
        var token = TestContext.Current.CancellationToken;

        await Stream(client.GetStreamingResponseAsync(_hello, cancellationToken: token));

        Assert.Equal(token, Assert.Single(gateway.Tokens));
    }

    [Fact]
    public async Task Every_streamed_call_goes_through_the_gateway_and_only_there()
    {
        var (client, gateway, provider) = Create();
        provider.EnqueueCall(new ScriptStep.Emit(new ModelEvent.Done(StopReason.EndTurn, "m")));
        provider.EnqueueCall(new ScriptStep.Emit(new ModelEvent.Done(StopReason.EndTurn, "m")));

        await Stream(client.GetStreamingResponseAsync(_hello, cancellationToken: TestContext.Current.CancellationToken));
        await client.GetResponseAsync(_hello, cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(2, gateway.CallCount);
        Assert.Equal(gateway.Requests, provider.Calls);
        Assert.Equal(0, provider.RemainingScripts);
    }

    [Fact]
    public void Constructor_takes_only_the_gateway_and_plain_identifiers()
    {
        var parameters = typeof(GatewayChatClient).GetConstructors().Single().GetParameters().Select(p => p.ParameterType).ToArray();

        Assert.Equal([typeof(IModelGateway), typeof(string), typeof(string), typeof(string)], parameters);
    }

    [Fact]
    public void Constructor_rejects_missing_gateway_and_blank_identifiers()
    {
        Assert.Throws<ArgumentNullException>(() => new GatewayChatClient(null!, "agent-1", _route));
        Assert.Throws<ArgumentException>(() => new GatewayChatClient(new RecordingGateway(new FakeProvider()), " ", _route));
        Assert.Throws<ArgumentException>(() => new GatewayChatClient(new RecordingGateway(new FakeProvider()), "agent-1", ""));
    }

    [Fact]
    public void GetService_returns_the_client_and_its_metadata_only()
    {
        var (client, _, _) = Create();

        Assert.Same(client, client.GetService<IChatClient>());
        var metadata = Assert.IsType<ChatClientMetadata>(client.GetService(typeof(ChatClientMetadata)));
        Assert.Equal("bazaar-gateway", metadata.ProviderName);
        Assert.Equal(_route, metadata.DefaultModelId);
        Assert.Null(client.GetService(typeof(IChatClient), "keyed"));
        Assert.Null(client.GetService(typeof(string)));
    }

    /// <summary>Forwards to a <see cref="FakeProvider"/> and records what the adapter asked of the gateway.</summary>
    private sealed class RecordingGateway(FakeProvider provider) : IModelGateway
    {
        private readonly List<ModelRequest> _requests = [];
        private readonly List<CancellationToken> _tokens = [];

        public IReadOnlyList<ModelRequest> Requests => _requests;

        public IReadOnlyList<CancellationToken> Tokens => _tokens;

        public int CallCount => _requests.Count;

        public IAsyncEnumerable<ModelEvent> CallAsync(ModelRequest request, CancellationToken cancellationToken)
        {
            _requests.Add(request);
            _tokens.Add(cancellationToken);
            return provider.StreamAsync(request, cancellationToken);
        }
    }
}
