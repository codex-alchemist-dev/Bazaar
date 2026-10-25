using System.Net;
using Bazaar.Contracts.Gateway;
using Bazaar.Server.Tests.Testing;
using Microsoft.Extensions.Time.Testing;
using static Bazaar.Server.Tests.Gateway.Adapters.OpenAi.OpenAiChatTestRig;

namespace Bazaar.Server.Tests.Gateway.Adapters.OpenAi;

public class OpenAiChatAdapterTests
{
    private static string ArgumentsFor(IEnumerable<ModelEvent> events, string callId) =>
        string.Concat(events.OfType<ModelEvent.ToolCallArgsDelta>().Where(e => e.CallId == callId).Select(e => e.ArgumentsFragment));

    [Fact]
    public async Task Plain_text_stream_maps_text_usage_and_done()
    {
        var handler = new StubHandler();
        handler.Reply(Replies.Sse(Fixture("text_stream.sse")));

        var events = await Run(handler);

        Assert.Equal("Hello, world.", TextOf(events));
        Assert.Equal(new GatewayTokenUsage(42, 0, 0, 0, 6, 0), Assert.Single(events.OfType<ModelEvent.UsageReported>()).Usage);
        Assert.Equal(new ModelEvent.Done(StopReason.EndTurn, "served-text-model"), events[^1]);
    }

    [Fact]
    public async Task Tool_call_split_across_chunks_is_assembled_into_one_call()
    {
        var handler = new StubHandler();
        handler.Reply(Replies.Sse(Fixture("tool_call_split.sse")));

        var events = await Run(handler);

        var start = Assert.Single(events.OfType<ModelEvent.ToolCallStart>());
        Assert.Equal(new ModelEvent.ToolCallStart("call_abc123", "get_weather"), start);
        Assert.Equal("{\"city\":\"Lisbon\"}", ArgumentsFor(events, "call_abc123"));
        Assert.Equal(3, events.OfType<ModelEvent.ToolCallArgsDelta>().Count());
        Assert.Equal(["call_abc123"], events.OfType<ModelEvent.ToolCallEnd>().Select(e => e.CallId));
        Assert.Equal(new ModelEvent.Done(StopReason.ToolUse, "served-tool-model"), events[^1]);
    }

    [Fact]
    public async Task Parallel_tool_calls_stay_apart_by_index()
    {
        var handler = new StubHandler();
        handler.Reply(Replies.Sse(Fixture("parallel_tool_calls.sse")));

        var events = await Run(handler);

        Assert.Equal(
            [new ModelEvent.ToolCallStart("call_1", "get_weather"), new ModelEvent.ToolCallStart("call_2", "get_time")],
            events.OfType<ModelEvent.ToolCallStart>());
        Assert.Equal("{\"city\":\"Oslo\"}", ArgumentsFor(events, "call_1"));
        Assert.Equal("{\"zone\":\"UTC\"}", ArgumentsFor(events, "call_2"));
        Assert.Equal(["call_1", "call_2"], events.OfType<ModelEvent.ToolCallEnd>().Select(e => e.CallId));
        Assert.Equal(StopReason.ToolUse, ((ModelEvent.Done)events[^1]).StopReason);
    }

    [Fact]
    public async Task Malformed_tool_json_ends_the_call_and_the_stream_with_a_failure()
    {
        var handler = new StubHandler();
        handler.Reply(Replies.Sse(Fixture("malformed_tool_json.sse")));

        var events = await Run(handler);

        Assert.Equal(["call_bad"], events.OfType<ModelEvent.ToolCallEnd>().Select(e => e.CallId));
        var failure = Assert.IsType<ModelEvent.Failure>(events[^1]);
        Assert.Equal("malformed_tool_json", failure.Code);
        Assert.DoesNotContain(events, e => e is ModelEvent.Done);
    }

    [Fact]
    public async Task Reasoning_and_cached_tokens_map_to_thinking_and_usage()
    {
        var handler = new StubHandler();
        handler.Reply(Replies.Sse(Fixture("reasoning_cached.sse")));

        var events = await Run(handler);

        Assert.Equal("The user asks about the units first.", string.Concat(events.OfType<ModelEvent.ThinkingDelta>().Select(e => e.Text)));
        Assert.Equal("It is 21 degrees.", TextOf(events));
        // Input is prompt tokens minus cached tokens; cache reads are reported separately; thinking is part of output.
        Assert.Equal(new GatewayTokenUsage(Input: 176, CacheWrite5m: 0, CacheWrite1h: 0, CacheRead: 1024, Output: 310, Thinking: 280),
            Assert.Single(events.OfType<ModelEvent.UsageReported>()).Usage);
        Assert.Equal(new ModelEvent.Done(StopReason.EndTurn, "served-reasoning-model"), events[^1]);
    }

    [Fact]
    public async Task Usage_without_detail_objects_counts_all_prompt_tokens_as_input_and_reads_the_reasoning_field()
    {
        const string sse = """
            data: {"model":"m","choices":[{"index":0,"delta":{"reasoning":"Think. "},"finish_reason":null}]}

            data: {"model":"m","choices":[{"index":0,"delta":{"content":"Done."},"finish_reason":"stop"}]}

            data: {"model":"m","choices":[],"usage":{"prompt_tokens":10,"completion_tokens":2}}

            data: [DONE]

            """;
        var handler = new StubHandler();
        handler.Reply(Replies.Sse(sse));

        var events = await Run(handler);

        Assert.Equal(new ModelEvent.ThinkingDelta("Think. "), events[0]);
        Assert.Equal(new GatewayTokenUsage(10, 0, 0, 0, 2, 0), Assert.Single(events.OfType<ModelEvent.UsageReported>()).Usage);
    }

    [Theory]
    [InlineData("stop", StopReason.EndTurn)]
    [InlineData("tool_calls", StopReason.ToolUse)]
    [InlineData("function_call", StopReason.ToolUse)]
    [InlineData("length", StopReason.MaxTokens)]
    [InlineData("content_filter", StopReason.ContentFilter)]
    [InlineData("something_new", StopReason.Other)]
    public async Task Finish_reasons_map_to_stop_reasons(string finishReason, StopReason expected)
    {
        var sse = $"data: {{\"model\":\"m\",\"choices\":[{{\"index\":0,\"delta\":{{\"content\":\"x\"}},\"finish_reason\":\"{finishReason}\"}}]}}\n\ndata: [DONE]\n\n";
        var handler = new StubHandler();
        handler.Reply(Replies.Sse(sse));

        var events = await Run(handler);

        Assert.Equal(new ModelEvent.Done(expected, "m"), events[^1]);
    }

    [Fact]
    public async Task Stream_that_finishes_without_done_still_completes()
    {
        const string sse = "data: {\"model\":\"m\",\"choices\":[{\"index\":0,\"delta\":{\"content\":\"ok\"},\"finish_reason\":\"stop\"}]}\n\n";
        var handler = new StubHandler();
        handler.Reply(Replies.Sse(sse));

        var events = await Run(handler);

        Assert.Equal(new ModelEvent.Done(StopReason.EndTurn, "m"), events[^1]);
    }

    [Fact]
    public async Task Done_names_the_requested_model_when_the_stream_names_none()
    {
        const string sse = "data: {\"choices\":[{\"index\":0,\"delta\":{\"content\":\"ok\"},\"finish_reason\":\"stop\"}]}\n\ndata: [DONE]\n\n";
        var handler = new StubHandler();
        handler.Reply(Replies.Sse(sse));

        var events = await Run(handler);

        Assert.Equal(new ModelEvent.Done(StopReason.EndTurn, Endpoint.ModelId), events[^1]);
    }

    [Fact]
    public async Task Rate_limit_429_reports_retry_after_seconds()
    {
        var handler = new StubHandler();
        handler.Reply(Replies.Error(HttpStatusCode.TooManyRequests, Fixture("rate_limited_429.json"), retryAfter: "30"));

        var failure = Assert.IsType<ModelEvent.Failure>(Assert.Single(await Run(handler)));

        Assert.Equal("rate_limited", failure.Code);
        Assert.Equal(30, failure.RetryAfterSeconds);
    }

    [Fact]
    public async Task Rate_limit_429_with_a_retry_after_date_counts_down_from_the_clock()
    {
        var clock = new FakeTimeProvider(new DateTimeOffset(2026, 10, 25, 12, 0, 0, TimeSpan.Zero));
        var handler = new StubHandler();
        handler.Reply(Replies.Error(HttpStatusCode.TooManyRequests, Fixture("rate_limited_429.json"), retryAfter: "Sun, 25 Oct 2026 12:02:00 GMT"));

        var failure = Assert.IsType<ModelEvent.Failure>(Assert.Single(await Run(handler, time: clock)));

        Assert.Equal("rate_limited", failure.Code);
        Assert.Equal(120, failure.RetryAfterSeconds);
    }

    [Fact]
    public async Task Spend_cap_429_is_spend_capped_with_no_retry_after()
    {
        var handler = new StubHandler();
        handler.Reply(Replies.Error(HttpStatusCode.TooManyRequests, Fixture("spend_cap_429.json"), retryAfter: "30"));

        var failure = Assert.IsType<ModelEvent.Failure>(Assert.Single(await Run(handler)));

        Assert.Equal("spend_capped", failure.Code);
        Assert.Null(failure.RetryAfterSeconds);
    }

    [Fact]
    public async Task Server_error_500_is_a_provider_error_and_does_not_throw()
    {
        var handler = new StubHandler();
        handler.Reply(Replies.Error(HttpStatusCode.InternalServerError, Fixture("server_error_500.json")));

        var failure = Assert.IsType<ModelEvent.Failure>(Assert.Single(await Run(handler)));

        Assert.Equal("provider_error", failure.Code);
        Assert.Contains("500", failure.Message);
    }

    [Fact]
    public async Task Upstream_that_never_answers_times_out_as_a_failure()
    {
        var handler = new StubHandler();
        handler.Reply(Replies.Hang());

        var failure = Assert.IsType<ModelEvent.Failure>(Assert.Single(await Run(handler, timeout: TimeSpan.FromMilliseconds(100))));

        Assert.Equal("timeout", failure.Code);
    }

    [Fact]
    public async Task Connection_refused_is_a_provider_error()
    {
        var handler = new StubHandler();
        handler.Reply(Replies.Refused());

        var failure = Assert.IsType<ModelEvent.Failure>(Assert.Single(await Run(handler)));

        Assert.Equal("provider_error", failure.Code);
    }

    [Fact]
    public async Task Stream_reset_after_partial_text_keeps_the_text_and_fails_as_dropped()
    {
        var handler = new StubHandler();
        handler.Reply(Replies.SseThenReset(Fixture("dropped_mid_stream.sse")));

        var events = await Run(handler);

        Assert.Equal("Partial answ", TextOf(events));
        var failure = Assert.IsType<ModelEvent.Failure>(events[^1]);
        Assert.Equal("stream_dropped", failure.Code);
        Assert.DoesNotContain(events, e => e is ModelEvent.Done);
    }

    [Fact]
    public async Task Clean_end_without_finish_or_done_is_a_dropped_stream()
    {
        var handler = new StubHandler();
        handler.Reply(Replies.Sse(Fixture("dropped_mid_stream.sse")));

        var events = await Run(handler);

        Assert.Equal("Partial answ", TextOf(events));
        Assert.Equal("stream_dropped", Assert.IsType<ModelEvent.Failure>(events[^1]).Code);
    }

    [Fact]
    public async Task In_stream_error_object_becomes_a_provider_error()
    {
        const string sse = "data: {\"error\":{\"message\":\"Overloaded, try later\",\"type\":\"server_error\"}}\n\n";
        var handler = new StubHandler();
        handler.Reply(Replies.Sse(sse));

        var failure = Assert.IsType<ModelEvent.Failure>(Assert.Single(await Run(handler)));

        Assert.Equal("provider_error", failure.Code);
        Assert.Contains("Overloaded", failure.Message);
    }

    [Fact]
    public async Task Chunk_that_is_not_json_is_a_provider_error()
    {
        const string sse = "data: {not json\n\n";
        var handler = new StubHandler();
        handler.Reply(Replies.Sse(sse));

        var failure = Assert.IsType<ModelEvent.Failure>(Assert.Single(await Run(handler)));

        Assert.Equal("provider_error", failure.Code);
    }

    [Fact]
    public async Task Caller_cancellation_propagates_instead_of_becoming_a_failure()
    {
        var handler = new StubHandler();
        handler.Reply(Replies.Hang());
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(50));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Run(handler, ct: cts.Token));
    }
}
