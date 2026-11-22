using Bazaar.Contracts.Gateway;
using Bazaar.Contracts.Numerics;
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

        Assert.Equal(TokenAmount.FromInt64(6), usage.Output);
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

    private static string UsageStream(string startInput, string deltaOutput) =>
        "event: message_start\n"
        + "data: {\"type\":\"message_start\",\"message\":{\"id\":\"m\",\"type\":\"message\",\"role\":\"assistant\",\"model\":\"claude-haiku-5-5\",\"content\":[],\"usage\":{\"input_tokens\":" + startInput + ",\"output_tokens\":1}}}\n\n"
        + "event: content_block_start\ndata: {\"type\":\"content_block_start\",\"index\":0,\"content_block\":{\"type\":\"text\",\"text\":\"\"}}\n\n"
        + "event: content_block_delta\ndata: {\"type\":\"content_block_delta\",\"index\":0,\"delta\":{\"type\":\"text_delta\",\"text\":\"Hi\"}}\n\n"
        + "event: content_block_stop\ndata: {\"type\":\"content_block_stop\",\"index\":0}\n\n"
        + "event: message_delta\n"
        + "data: {\"type\":\"message_delta\",\"delta\":{\"stop_reason\":\"end_turn\"},\"usage\":{\"output_tokens\":" + deltaOutput + "}}\n\n"
        + "event: message_stop\ndata: {\"type\":\"message_stop\"}\n\n";

    private static async Task<List<ModelEvent>> Events(string stream)
    {
        using var handler = new StubHandler(_ => AnthropicTestSupport.Sse(stream));
        var adapter = AnthropicTestSupport.Adapter(handler);
        return await AnthropicTestSupport.Collect(adapter, AnthropicTestSupport.Endpoint(), AnthropicTestSupport.SimpleRequest(), TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task A_count_past_the_long_range_is_read_exactly()
    {
        var events = await Events(UsageStream("92233720368547758070", "4"));

        var usage = Assert.Single(events.OfType<ModelEvent.UsageReported>()).Usage;
        Assert.Equal(TokenAmount.Parse("92233720368547758070"), usage.Input);
        Assert.Equal(TokenAmount.FromInt64(4), usage.Output);
        Assert.IsType<ModelEvent.Done>(events[^1]);
    }

    [Theory]
    [InlineData("1.5e3", "1500")]
    [InlineData("12.5", "12.5")]
    [InlineData("7", "7")]
    public async Task A_decimal_or_exponent_count_is_read_exactly_never_through_a_float(string wire, string exact)
    {
        var usage = Assert.Single((await Events(UsageStream(wire, "4"))).OfType<ModelEvent.UsageReported>()).Usage;

        Assert.Equal(TokenAmount.Parse(exact), usage.Input);
    }

    [Theory]
    [InlineData("-5", "4")]
    [InlineData("-92233720368547758070", "4")]
    [InlineData("10", "-1")]
    [InlineData("1e999999999", "4")]
    public async Task A_negative_or_absurd_count_drops_the_report_and_the_call_still_completes(string input, string output)
    {
        var events = await Events(UsageStream(input, output));

        Assert.DoesNotContain(events, e => e is ModelEvent.UsageReported);
        Assert.IsType<ModelEvent.Done>(events[^1]);
    }

    [Fact]
    public async Task A_count_beyond_the_numeric_digit_bounds_drops_the_report()
    {
        var events = await Events(UsageStream(new string('9', 131_073), "4"));

        Assert.DoesNotContain(events, e => e is ModelEvent.UsageReported);
        Assert.IsType<ModelEvent.Done>(events[^1]);
    }
}
