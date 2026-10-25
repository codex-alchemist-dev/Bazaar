using System.Net;
using System.Text;
using System.Text.Json;
using Bazaar.Contracts.Gateway;

namespace Bazaar.Server.Tests.Gateway.Adapters.Anthropic;

public class AnthropicMessagesStreamTests
{
    [Fact]
    public async Task Text_stream_yields_text_usage_and_done_with_served_model()
    {
        using var handler = new StubHandler(_ => AnthropicTestSupport.Sse(AnthropicTestSupport.Fixture("text_stream.sse")));
        var adapter = AnthropicTestSupport.Adapter(handler);

        var events = await AnthropicTestSupport.Collect(adapter, AnthropicTestSupport.Endpoint(), AnthropicTestSupport.SimpleRequest(), TestContext.Current.CancellationToken);

        Assert.Equal(
            [
                new ModelEvent.TextDelta("Hello"),
                new ModelEvent.TextDelta(", world"),
                new ModelEvent.UsageReported(new GatewayTokenUsage(25, 0, 0, 0, 6, 0)),
                new ModelEvent.Done(StopReason.EndTurn, "claude-haiku-5-5-20261001"),
            ],
            events);
    }

    [Fact]
    public async Task Tool_call_arguments_split_across_deltas_assemble_into_valid_json()
    {
        using var handler = new StubHandler(_ => AnthropicTestSupport.Sse(AnthropicTestSupport.Fixture("tool_use_split.sse")));
        var adapter = AnthropicTestSupport.Adapter(handler);

        var events = await AnthropicTestSupport.Collect(adapter, AnthropicTestSupport.Endpoint(), AnthropicTestSupport.SimpleRequest(), TestContext.Current.CancellationToken);

        Assert.Equal(new ModelEvent.TextDelta("I'll check the weather in Paris."), events[0]);
        Assert.Equal(new ModelEvent.ToolCallStart("toolu_01A09q90qw90lq917835lq9", "get_weather"), events[1]);

        // The empty first partial_json is not forwarded; the three real fragments are, in order.
        var fragments = events.OfType<ModelEvent.ToolCallArgsDelta>().Select(d => d.ArgumentsFragment).ToArray();
        Assert.Equal(["{\"city\": \"Pa", "ris\", \"unit\": \"cel", "sius\"}"], fragments);
        Assert.All(events.OfType<ModelEvent.ToolCallArgsDelta>(), d => Assert.Equal("toolu_01A09q90qw90lq917835lq9", d.CallId));

        var assembled = string.Concat(fragments);
        using (var document = JsonDocument.Parse(assembled))
        {
            Assert.Equal("Paris", document.RootElement.GetProperty("city").GetString());
            Assert.Equal("celsius", document.RootElement.GetProperty("unit").GetString());
        }

        Assert.Equal(new ModelEvent.ToolCallEnd("toolu_01A09q90qw90lq917835lq9"), events[^3]);
        Assert.Equal(new ModelEvent.UsageReported(new GatewayTokenUsage(480, 0, 0, 0, 38, 0)), events[^2]);
        Assert.Equal(new ModelEvent.Done(StopReason.ToolUse, "claude-haiku-5-5-20261001"), events[^1]);
    }

    [Fact]
    public async Task Thinking_deltas_are_forwarded_and_signature_is_dropped()
    {
        using var handler = new StubHandler(_ => AnthropicTestSupport.Sse(AnthropicTestSupport.Fixture("thinking.sse")));
        var adapter = AnthropicTestSupport.Adapter(handler);

        var events = await AnthropicTestSupport.Collect(adapter, AnthropicTestSupport.Endpoint(), AnthropicTestSupport.SimpleRequest(), TestContext.Current.CancellationToken);

        Assert.Equal(
            [
                new ModelEvent.ThinkingDelta("Let me"),
                new ModelEvent.ThinkingDelta(" think."),
                new ModelEvent.TextDelta("Answer."),
                // Thinking tokens are inside output_tokens; the thinking count stays 0 for Anthropic.
                new ModelEvent.UsageReported(new GatewayTokenUsage(60, 0, 0, 0, 120, 0)),
                new ModelEvent.Done(StopReason.EndTurn, "claude-haiku-5-5-20261001"),
            ],
            events);
    }

    [Fact]
    public async Task Ping_events_are_ignored()
    {
        using var handler = new StubHandler(_ => AnthropicTestSupport.Sse(AnthropicTestSupport.Fixture("text_stream.sse")));
        var adapter = AnthropicTestSupport.Adapter(handler);

        var events = await AnthropicTestSupport.Collect(adapter, AnthropicTestSupport.Endpoint(), AnthropicTestSupport.SimpleRequest(), TestContext.Current.CancellationToken);

        Assert.DoesNotContain(events, e => e is ModelEvent.Failure or ModelEvent.Warning);
        Assert.Single(events.OfType<ModelEvent.Done>());
    }

    [Theory]
    [InlineData("end_turn", StopReason.EndTurn)]
    [InlineData("stop_sequence", StopReason.EndTurn)]
    [InlineData("tool_use", StopReason.ToolUse)]
    [InlineData("max_tokens", StopReason.MaxTokens)]
    [InlineData("refusal", StopReason.ContentFilter)]
    [InlineData("pause_turn", StopReason.Other)]
    public async Task Stop_reasons_map_to_the_contract(string wire, StopReason expected)
    {
        using var handler = new StubHandler(_ => AnthropicTestSupport.Sse(AnthropicTestSupport.TextStreamWithStopReason(wire)));
        var adapter = AnthropicTestSupport.Adapter(handler);

        var events = await AnthropicTestSupport.Collect(adapter, AnthropicTestSupport.Endpoint(), AnthropicTestSupport.SimpleRequest(), TestContext.Current.CancellationToken);

        Assert.Equal(new ModelEvent.Done(expected, "claude-haiku-5-5-20261001"), events[^1]);
    }

    [Fact]
    public async Task Stream_that_ends_before_message_stop_is_reported_as_dropped_with_partial_usage()
    {
        using var handler = new StubHandler(_ => AnthropicTestSupport.Sse(AnthropicTestSupport.Fixture("text_stream_truncated.sse")));
        var adapter = AnthropicTestSupport.Adapter(handler);

        var events = await AnthropicTestSupport.Collect(adapter, AnthropicTestSupport.Endpoint(), AnthropicTestSupport.SimpleRequest(), TestContext.Current.CancellationToken);

        Assert.Equal(
            [
                new ModelEvent.TextDelta("Hello"),
                new ModelEvent.UsageReported(new GatewayTokenUsage(25, 0, 0, 0, 1, 0)),
                new ModelEvent.Failure("stream_dropped", "The stream ended before the response finished."),
            ],
            events);
    }

    [Fact]
    public async Task Connection_reset_mid_body_is_reported_as_dropped_and_does_not_throw()
    {
        var prefix = Encoding.UTF8.GetBytes(AnthropicTestSupport.Fixture("text_stream_truncated.sse"));
        using var handler = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StreamContent(new ResetAfterStream(prefix)),
        });
        var adapter = AnthropicTestSupport.Adapter(handler);

        var events = await AnthropicTestSupport.Collect(adapter, AnthropicTestSupport.Endpoint(), AnthropicTestSupport.SimpleRequest(), TestContext.Current.CancellationToken);

        var failure = Assert.IsType<ModelEvent.Failure>(events[^1]);
        Assert.Equal("stream_dropped", failure.Code);
        Assert.Equal(new ModelEvent.TextDelta("Hello"), events[0]);
    }

    [Fact]
    public async Task Stream_error_event_ends_the_call_with_partial_usage()
    {
        using var handler = new StubHandler(_ => AnthropicTestSupport.Sse(AnthropicTestSupport.Fixture("midstream_error.sse")));
        var adapter = AnthropicTestSupport.Adapter(handler);

        var events = await AnthropicTestSupport.Collect(adapter, AnthropicTestSupport.Endpoint(), AnthropicTestSupport.SimpleRequest(), TestContext.Current.CancellationToken);

        Assert.Equal(
            [
                new ModelEvent.TextDelta("Partial answer"),
                new ModelEvent.UsageReported(new GatewayTokenUsage(33, 0, 0, 0, 1, 0)),
                new ModelEvent.Failure("provider_error", "Overloaded"),
            ],
            events);
    }

    [Fact]
    public async Task Malformed_tool_arguments_fail_the_call_without_a_tool_call_end()
    {
        var stream = AnthropicTestSupport.Event("message_start", """{"type":"message_start","message":{"model":"claude-haiku-5-5-20261001","usage":{"input_tokens":12,"output_tokens":1}}}""")
            + AnthropicTestSupport.Event("content_block_start", """{"type":"content_block_start","index":0,"content_block":{"type":"tool_use","id":"toolu_bad","name":"get_weather","input":{}}}""")
            + AnthropicTestSupport.Event("content_block_delta", """{"type":"content_block_delta","index":0,"delta":{"type":"input_json_delta","partial_json":"{\"city\": \"Par"}}""")
            + AnthropicTestSupport.Event("content_block_stop", """{"type":"content_block_stop","index":0}""")
            + AnthropicTestSupport.Event("message_stop", """{"type":"message_stop"}""");
        using var handler = new StubHandler(_ => AnthropicTestSupport.Sse(stream));
        var adapter = AnthropicTestSupport.Adapter(handler);

        var events = await AnthropicTestSupport.Collect(adapter, AnthropicTestSupport.Endpoint(), AnthropicTestSupport.SimpleRequest(), TestContext.Current.CancellationToken);

        Assert.Equal(
            [
                new ModelEvent.ToolCallStart("toolu_bad", "get_weather"),
                new ModelEvent.ToolCallArgsDelta("toolu_bad", "{\"city\": \"Par"),
                new ModelEvent.UsageReported(new GatewayTokenUsage(12, 0, 0, 0, 1, 0)),
                new ModelEvent.Failure("malformed_tool_json", "Tool call 'toolu_bad' has arguments that are not a valid JSON object."),
            ],
            events);
    }

    [Fact]
    public async Task Tool_call_without_arguments_gets_an_empty_object()
    {
        var stream = AnthropicTestSupport.Event("message_start", """{"type":"message_start","message":{"model":"claude-haiku-5-5-20261001","usage":{"input_tokens":12,"output_tokens":1}}}""")
            + AnthropicTestSupport.Event("content_block_start", """{"type":"content_block_start","index":0,"content_block":{"type":"tool_use","id":"toolu_none","name":"list_items","input":{}}}""")
            + AnthropicTestSupport.Event("content_block_stop", """{"type":"content_block_stop","index":0}""")
            + AnthropicTestSupport.Event("message_delta", """{"type":"message_delta","delta":{"stop_reason":"tool_use","stop_sequence":null},"usage":{"output_tokens":4}}""")
            + AnthropicTestSupport.Event("message_stop", """{"type":"message_stop"}""");
        using var handler = new StubHandler(_ => AnthropicTestSupport.Sse(stream));
        var adapter = AnthropicTestSupport.Adapter(handler);

        var events = await AnthropicTestSupport.Collect(adapter, AnthropicTestSupport.Endpoint(), AnthropicTestSupport.SimpleRequest(), TestContext.Current.CancellationToken);

        Assert.Equal(new ModelEvent.ToolCallStart("toolu_none", "list_items"), events[0]);
        Assert.Equal(new ModelEvent.ToolCallEnd("toolu_none"), events[1]);
        Assert.Equal(new ModelEvent.Done(StopReason.ToolUse, "claude-haiku-5-5-20261001"), events[^1]);
    }

    [Fact]
    public async Task Served_model_falls_back_to_the_requested_model_when_message_start_omits_it()
    {
        var stream = AnthropicTestSupport.Event("message_start", """{"type":"message_start","message":{"usage":{"input_tokens":1,"output_tokens":1}}}""")
            + AnthropicTestSupport.Event("message_stop", """{"type":"message_stop"}""");
        using var handler = new StubHandler(_ => AnthropicTestSupport.Sse(stream));
        var adapter = AnthropicTestSupport.Adapter(handler);

        var events = await AnthropicTestSupport.Collect(adapter, AnthropicTestSupport.Endpoint(), AnthropicTestSupport.SimpleRequest(), TestContext.Current.CancellationToken);

        Assert.Equal(new ModelEvent.Done(StopReason.Other, AnthropicTestSupport.RequestedModel), events[^1]);
    }

    [Fact]
    public async Task Event_data_that_is_not_json_is_a_provider_error()
    {
        var stream = AnthropicTestSupport.Event("content_block_delta", "not json at all");
        using var handler = new StubHandler(_ => AnthropicTestSupport.Sse(stream));
        var adapter = AnthropicTestSupport.Adapter(handler);

        var events = await AnthropicTestSupport.Collect(adapter, AnthropicTestSupport.Endpoint(), AnthropicTestSupport.SimpleRequest(), TestContext.Current.CancellationToken);

        Assert.Equal(new ModelEvent.Failure("provider_error", "The upstream sent an event that is not valid JSON."), Assert.Single(events));
    }
}
