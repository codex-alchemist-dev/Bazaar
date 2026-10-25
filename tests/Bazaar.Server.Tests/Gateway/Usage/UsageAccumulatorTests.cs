using System.Reflection;
using Bazaar.Contracts.Gateway;
using Bazaar.Server.Gateway.Fakes;
using Bazaar.Server.Gateway.Usage;
using Microsoft.Extensions.Time.Testing;

namespace Bazaar.Server.Tests.Gateway.Usage;

public class UsageAccumulatorTests
{
    private static readonly DateTimeOffset _start = new(2026, 10, 25, 12, 0, 0, TimeSpan.Zero);

    private static ModelRequest Request() =>
        new("agent-1", "route-a", [new ModelMessage(ModelRole.User, "hello")], []);

    private static UsageAccumulator Accumulator(
        TimeProvider? time = null,
        GatewayIdentity identity = GatewayIdentity.Native,
        long estimatedInputTokens = 0) =>
        new("agent-1", "turn-1", "anthropic-direct", "keyref-anthropic-1", "claude-haiku-5-5",
            identity, time ?? new FakeTimeProvider(_start), estimatedInputTokens);

    private static async Task Feed(UsageAccumulator accumulator, IAsyncEnumerable<ModelEvent> stream)
    {
        await foreach (var modelEvent in stream)
        {
            accumulator.Observe(modelEvent);
        }
    }

    [Fact]
    public async Task A_completed_stream_is_a_success_with_the_reported_usage()
    {
        var provider = new FakeProvider();
        var usage = new GatewayTokenUsage(Input: 100, CacheWrite5m: 0, CacheWrite1h: 0, CacheRead: 0, Output: 20, Thinking: 0);
        provider.EnqueueCall(
            new ScriptStep.Emit(new ModelEvent.TextDelta("Hello")),
            new ScriptStep.Emit(new ModelEvent.UsageReported(usage)),
            new ScriptStep.Emit(new ModelEvent.Done(StopReason.EndTurn, "served-1")));
        var accumulator = Accumulator();

        await Feed(accumulator, provider.StreamAsync(Request(), TestContext.Current.CancellationToken));
        var record = accumulator.Build(null);

        Assert.Equal(UsageStatus.Success, record.Status);
        Assert.Equal("served-1", record.ServedModel);
        Assert.Equal(usage, record.Tokens);
        Assert.False(record.Estimated);
        Assert.Null(record.ErrorCode);
        Assert.Null(record.RetryAfterSeconds);
        Assert.Equal("agent-1", record.AgentId);
        Assert.Equal("turn-1", record.TurnId);
        Assert.Equal("anthropic-direct", record.ProviderId);
        Assert.Equal("keyref-anthropic-1", record.KeyRef);
        Assert.Equal("claude-haiku-5-5", record.ModelId);
    }

    [Fact]
    public async Task A_failure_after_text_is_an_error_with_estimated_tokens()
    {
        var provider = new FakeProvider();
        provider.EnqueueCall(
            new ScriptStep.Emit(new ModelEvent.TextDelta("abcdefgh")),
            new ScriptStep.RateLimited(RetryAfterSeconds: 30));
        var accumulator = Accumulator(estimatedInputTokens: 500);

        await Feed(accumulator, provider.StreamAsync(Request(), TestContext.Current.CancellationToken));
        var record = accumulator.Build(null);

        Assert.Equal(UsageStatus.Error, record.Status);
        Assert.Equal("rate_limited", record.ErrorCode);
        Assert.Equal(30, record.RetryAfterSeconds);
        Assert.True(record.Estimated);
        Assert.Null(record.ServedModel);
        // Eight characters at four per token.
        Assert.Equal(new GatewayTokenUsage(500, 0, 0, 0, Output: 2, Thinking: 0), record.Tokens);
    }

    [Fact]
    public async Task A_spend_cap_error_has_no_retry_after()
    {
        var provider = new FakeProvider();
        provider.EnqueueCall(new ScriptStep.SpendCapped());
        var accumulator = Accumulator();

        await Feed(accumulator, provider.StreamAsync(Request(), TestContext.Current.CancellationToken));
        var record = accumulator.Build(null);

        Assert.Equal(UsageStatus.Error, record.Status);
        Assert.Equal("spend_capped", record.ErrorCode);
        Assert.Null(record.RetryAfterSeconds);
    }

    [Fact]
    public async Task A_cancelled_stream_is_aborted_and_keeps_the_partial_text_count()
    {
        var time = new FakeTimeProvider(_start);
        var provider = new FakeProvider(time);
        provider.EnqueueCall(
            new ScriptStep.Emit(new ModelEvent.TextDelta("partial")),
            new ScriptStep.TimedOut(TimeSpan.FromMinutes(5)));
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        var accumulator = Accumulator(time);

        var stream = provider.StreamAsync(Request(), cts.Token);
        await using var events = stream.GetAsyncEnumerator(TestContext.Current.CancellationToken);
        Assert.True(await events.MoveNextAsync());
        accumulator.Observe(events.Current);
        await cts.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await events.MoveNextAsync());

        var record = accumulator.Build(null);

        Assert.Equal(UsageStatus.Aborted, record.Status);
        Assert.True(record.Estimated);
        Assert.Null(record.ErrorCode);
        // Seven characters round up to two tokens.
        Assert.Equal(2, record.Tokens.Output);
    }

    [Fact]
    public void An_aborted_stream_stays_estimated_even_when_usage_was_reported()
    {
        var reported = new GatewayTokenUsage(Input: 500, CacheWrite5m: 0, CacheWrite1h: 0, CacheRead: 0, Output: 40, Thinking: 0);
        var accumulator = Accumulator();

        accumulator.Observe(new ModelEvent.UsageReported(reported));
        var record = accumulator.Build(null);

        Assert.Equal(UsageStatus.Aborted, record.Status);
        Assert.True(record.Estimated);
        Assert.Equal(reported, record.Tokens);
    }

    [Fact]
    public void Without_reported_usage_a_success_is_estimated_from_character_counts()
    {
        var accumulator = Accumulator(estimatedInputTokens: 1234);

        accumulator.Observe(new ModelEvent.TextDelta("abcd"));
        accumulator.Observe(new ModelEvent.ThinkingDelta("123456789"));
        accumulator.Observe(new ModelEvent.ToolCallStart("call-1", "search"));
        accumulator.Observe(new ModelEvent.ToolCallArgsDelta("call-1", "{\"q\":1}"));
        accumulator.Observe(new ModelEvent.ToolCallEnd("call-1"));
        accumulator.Observe(new ModelEvent.Done(StopReason.ToolUse, "served-2"));
        var record = accumulator.Build(null);

        Assert.Equal(UsageStatus.Success, record.Status);
        Assert.True(record.Estimated);
        // Text 4 + thinking 9 + arguments 7 = 20 characters, so 5 output tokens. The tool name is not counted.
        // Thinking is 9 characters, so 3 tokens.
        Assert.Equal(new GatewayTokenUsage(Input: 1234, CacheWrite5m: 0, CacheWrite1h: 0, CacheRead: 0, Output: 5, Thinking: 3), record.Tokens);
    }

    [Fact]
    public void Estimated_output_rounds_up_partial_tokens()
    {
        var accumulator = Accumulator();

        accumulator.Observe(new ModelEvent.TextDelta("a"));
        accumulator.Observe(new ModelEvent.Done(StopReason.EndTurn, "m"));
        var record = accumulator.Build(null);

        Assert.Equal(1, record.Tokens.Output);
        Assert.Equal(0, record.Tokens.Thinking);
    }

    [Fact]
    public void Exact_usage_keeps_input_as_given_and_excludes_cache_reads()
    {
        var reported = new GatewayTokenUsage(Input: 100, CacheWrite5m: 0, CacheWrite1h: 0, CacheRead: 5000, Output: 30, Thinking: 10);
        var accumulator = Accumulator();

        accumulator.Observe(new ModelEvent.TextDelta("text that is not counted as tokens when usage is exact"));
        accumulator.Observe(new ModelEvent.UsageReported(reported));
        accumulator.Observe(new ModelEvent.Done(StopReason.EndTurn, "m"));
        var record = accumulator.Build(null);

        Assert.Equal(reported, record.Tokens);
        Assert.Equal(100, record.Tokens.Input);
        Assert.False(record.Estimated);
    }

    [Fact]
    public void A_second_usage_report_replaces_the_first()
    {
        var accumulator = Accumulator();

        accumulator.Observe(new ModelEvent.UsageReported(new GatewayTokenUsage(10, 0, 0, 0, 1, 0)));
        accumulator.Observe(new ModelEvent.UsageReported(new GatewayTokenUsage(99, 0, 0, 0, 7, 0)));
        accumulator.Observe(new ModelEvent.Done(StopReason.EndTurn, "m"));
        var record = accumulator.Build(null);

        Assert.Equal(99, record.Tokens.Input);
        Assert.Equal(7, record.Tokens.Output);
        Assert.False(record.Estimated);
    }

    [Theory]
    [InlineData(GatewayIdentity.Native)]
    [InlineData(GatewayIdentity.Pooled)]
    public void The_identity_label_is_carried_onto_the_row(GatewayIdentity identity)
    {
        var accumulator = Accumulator(identity: identity);

        accumulator.Observe(new ModelEvent.Done(StopReason.EndTurn, "m"));

        Assert.Equal(identity, accumulator.Build(null).Identity);
    }

    [Fact]
    public void Latency_is_measured_on_the_time_provider_and_the_timestamp_is_the_start()
    {
        var time = new FakeTimeProvider(_start);
        var accumulator = Accumulator(time);

        time.Advance(TimeSpan.FromMilliseconds(250));
        var record = accumulator.Build(null);

        Assert.Equal(250, record.LatencyMs);
        Assert.Equal(_start, record.Timestamp);
    }

    [Fact]
    public void Price_version_is_set_only_when_a_price_applies_at_the_timestamp()
    {
        var accumulator = Accumulator();
        accumulator.Observe(new ModelEvent.Done(StopReason.EndTurn, "m"));
        var priced = new PriceTable("2025-01", [new PriceEntry("anthropic-direct", "claude-haiku-5-5", new DateOnly(2025, 1, 1), 1m, 5m)]);
        var notYetPriced = new PriceTable("2030-01", [new PriceEntry("anthropic-direct", "claude-haiku-5-5", new DateOnly(2030, 1, 1), 1m, 5m)]);
        var otherModel = new PriceTable("other", [new PriceEntry("anthropic-direct", "claude-sonnet-5-5", new DateOnly(2025, 1, 1), 1m, 5m)]);

        Assert.Equal("2025-01", accumulator.Build(priced).PriceVersion);
        Assert.Null(accumulator.Build(notYetPriced).PriceVersion);
        Assert.Null(accumulator.Build(otherModel).PriceVersion);
        Assert.Null(accumulator.Build(null).PriceVersion);
    }

    [Fact]
    public void No_text_from_the_stream_is_kept_in_the_row()
    {
        const string Secret = "SECRET-PROMPT-SENTINEL";
        var accumulator = Accumulator();

        accumulator.Observe(new ModelEvent.TextDelta($"visible {Secret}"));
        accumulator.Observe(new ModelEvent.ThinkingDelta($"reasoning {Secret}"));
        accumulator.Observe(new ModelEvent.ToolCallStart("call-1", $"tool {Secret}"));
        accumulator.Observe(new ModelEvent.ToolCallArgsDelta("call-1", $"{{\"q\":\"{Secret}\"}}"));
        accumulator.Observe(new ModelEvent.Failure("provider_error", $"upstream said {Secret}"));
        var record = accumulator.Build(new PriceTable("v", [new PriceEntry("anthropic-direct", "claude-haiku-5-5", new DateOnly(2025, 1, 1), 1m, 5m)]));

        var stringValues = typeof(GatewayUsageRecord).GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Select(p => p.GetValue(record))
            .OfType<string>();
        Assert.All(stringValues, value => Assert.DoesNotContain(Secret, value));
        Assert.DoesNotContain(
            typeof(GatewayUsageRecord).GetProperties(BindingFlags.Public | BindingFlags.Instance),
            p => p.Name.Contains("Text", StringComparison.Ordinal) || p.Name.Contains("Prompt", StringComparison.Ordinal) || p.Name.Contains("Completion", StringComparison.Ordinal));
    }
}
