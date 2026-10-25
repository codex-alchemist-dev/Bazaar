using Bazaar.Contracts.Gateway;

namespace Bazaar.Server.Tests.Gateway.Adapters.Anthropic;

/// <summary>Usage mapping. Cache writes stay split into the 5-minute and 1-hour buckets (decision C33).</summary>
public class AnthropicMessagesUsageTests
{
    private static async Task<List<ModelEvent.UsageReported>> UsageEvents(string stream)
    {
        using var handler = new StubHandler(_ => AnthropicTestSupport.Sse(stream));
        var adapter = AnthropicTestSupport.Adapter(handler);
        var events = await AnthropicTestSupport.Collect(adapter, AnthropicTestSupport.Endpoint(), AnthropicTestSupport.SimpleRequest(), TestContext.Current.CancellationToken);
        return events.OfType<ModelEvent.UsageReported>().ToList();
    }

    [Fact]
    public async Task Cache_writes_with_5m_and_1h_breakdown_stay_in_separate_buckets()
    {
        using var handler = new StubHandler(_ => AnthropicTestSupport.Sse(AnthropicTestSupport.Fixture("cache_write_5m_1h.sse")));
        var adapter = AnthropicTestSupport.Adapter(handler);

        var events = await AnthropicTestSupport.Collect(adapter, AnthropicTestSupport.Endpoint(), AnthropicTestSupport.SimpleRequest(), TestContext.Current.CancellationToken);

        // The flat cache_creation_input_tokens (2000) is the sum of the buckets, so it must not be counted again.
        var usage = Assert.Single(events.OfType<ModelEvent.UsageReported>()).Usage;
        Assert.Equal(new GatewayTokenUsage(Input: 42, CacheWrite5m: 1200, CacheWrite1h: 800, CacheRead: 5000, Output: 77, Thinking: 0), usage);
    }

    [Fact]
    public async Task Flat_cache_creation_without_breakdown_is_attributed_to_5m()
    {
        var usage = Assert.Single(await UsageEvents(AnthropicTestSupport.Fixture("cache_write_flat_only.sse"))).Usage;

        Assert.Equal(new GatewayTokenUsage(Input: 30, CacheWrite5m: 900, CacheWrite1h: 0, CacheRead: 0, Output: 9, Thinking: 0), usage);
    }

    [Fact]
    public async Task Usage_is_emitted_once_and_before_done()
    {
        using var handler = new StubHandler(_ => AnthropicTestSupport.Sse(AnthropicTestSupport.Fixture("tool_use_split.sse")));
        var adapter = AnthropicTestSupport.Adapter(handler);

        var events = await AnthropicTestSupport.Collect(adapter, AnthropicTestSupport.Endpoint(), AnthropicTestSupport.SimpleRequest(), TestContext.Current.CancellationToken);

        Assert.Single(events.OfType<ModelEvent.UsageReported>());
        Assert.IsType<ModelEvent.UsageReported>(events[^2]);
        Assert.IsType<ModelEvent.Done>(events[^1]);
    }

    [Fact]
    public async Task Message_delta_output_is_cumulative_and_replaces_the_start_value()
    {
        // message_start reports output 1; message_delta reports the running total 6, which must win.
        var usage = Assert.Single(await UsageEvents(AnthropicTestSupport.Fixture("text_stream.sse"))).Usage;

        Assert.Equal(6, usage.Output);
    }

    [Fact]
    public async Task Message_delta_keeps_start_input_and_cache_when_it_omits_them()
    {
        var stream = AnthropicTestSupport.Event("message_start", """{"type":"message_start","message":{"model":"claude-haiku-5-5-20261001","usage":{"input_tokens":10,"cache_read_input_tokens":300,"cache_creation":{"ephemeral_5m_input_tokens":40,"ephemeral_1h_input_tokens":0},"output_tokens":1}}}""")
            + AnthropicTestSupport.Event("message_delta", """{"type":"message_delta","delta":{"stop_reason":"end_turn","stop_sequence":null},"usage":{"output_tokens":5}}""")
            + AnthropicTestSupport.Event("message_stop", """{"type":"message_stop"}""");

        var usage = Assert.Single(await UsageEvents(stream)).Usage;

        Assert.Equal(new GatewayTokenUsage(Input: 10, CacheWrite5m: 40, CacheWrite1h: 0, CacheRead: 300, Output: 5, Thinking: 0), usage);
    }

    [Fact]
    public async Task Message_delta_input_and_cache_fields_override_the_start_values_when_present()
    {
        var stream = AnthropicTestSupport.Event("message_start", """{"type":"message_start","message":{"model":"claude-haiku-5-5-20261001","usage":{"input_tokens":10,"output_tokens":1}}}""")
            + AnthropicTestSupport.Event("message_delta", """{"type":"message_delta","delta":{"stop_reason":"end_turn","stop_sequence":null},"usage":{"input_tokens":12,"cache_read_input_tokens":7,"output_tokens":5}}""")
            + AnthropicTestSupport.Event("message_stop", """{"type":"message_stop"}""");

        var usage = Assert.Single(await UsageEvents(stream)).Usage;

        Assert.Equal(new GatewayTokenUsage(Input: 12, CacheWrite5m: 0, CacheWrite1h: 0, CacheRead: 7, Output: 5, Thinking: 0), usage);
    }

    [Fact]
    public async Task Breakdown_wins_over_a_flat_total_when_both_are_present()
    {
        var stream = AnthropicTestSupport.Event("message_start", """{"type":"message_start","message":{"model":"claude-haiku-5-5-20261001","usage":{"input_tokens":1,"cache_creation_input_tokens":999,"cache_creation":{"ephemeral_5m_input_tokens":0,"ephemeral_1h_input_tokens":500},"output_tokens":1}}}""")
            + AnthropicTestSupport.Event("message_stop", """{"type":"message_stop"}""");

        var usage = Assert.Single(await UsageEvents(stream)).Usage;

        Assert.Equal(new GatewayTokenUsage(Input: 1, CacheWrite5m: 0, CacheWrite1h: 500, CacheRead: 0, Output: 1, Thinking: 0), usage);
    }

    [Fact]
    public async Task Partial_usage_is_reported_before_a_mid_stream_failure()
    {
        var usage = Assert.Single(await UsageEvents(AnthropicTestSupport.Fixture("midstream_error.sse"))).Usage;

        Assert.Equal(new GatewayTokenUsage(Input: 33, CacheWrite5m: 0, CacheWrite1h: 0, CacheRead: 0, Output: 1, Thinking: 0), usage);
    }

    [Fact]
    public async Task Upstream_error_before_any_event_reports_no_usage()
    {
        using var handler = new StubHandler(_ => AnthropicTestSupport.Json(System.Net.HttpStatusCode.InternalServerError,
            """{"type":"error","error":{"type":"api_error","message":"Internal server error"}}"""));
        var adapter = AnthropicTestSupport.Adapter(handler);

        var events = await AnthropicTestSupport.Collect(adapter, AnthropicTestSupport.Endpoint(), AnthropicTestSupport.SimpleRequest(), TestContext.Current.CancellationToken);

        Assert.DoesNotContain(events, e => e is ModelEvent.UsageReported);
    }
}
