using System.Text.Json;
using System.Text.Json.Serialization;

using Bazaar.Contracts.Gateway;
using Bazaar.Contracts.Numerics;

namespace Bazaar.Contracts.Tests.Gateway;

public class GatewayContractsTests
{
    private static readonly DateTimeOffset _moment = new(2026, 10, 25, 5, 37, 0, TimeSpan.Zero);

    private static string Serialize<T>(T value) => JsonSerializer.Serialize(value, GatewayJson.Options);

    private static T Deserialize<T>(string json) =>
        JsonSerializer.Deserialize<T>(json, GatewayJson.Options) ?? throw new InvalidOperationException("null JSON");

    [Fact]
    public void Model_request_round_trips_with_tools_and_tool_calls()
    {
        var request = new ModelRequest(
            AgentId: "agent-charles",
            Route: "custom/code",
            Messages:
            [
                new ModelMessage(ModelRole.System, "You are Charles."),
                new ModelMessage(ModelRole.User, "List the open PRs."),
                new ModelMessage(
                    ModelRole.Assistant,
                    "",
                    ToolCalls: [new ModelToolCall("call_1", "list_prs", "{\"state\":\"open\"}", ProviderData: "opaque-token")]),
                new ModelMessage(ModelRole.Tool, "[]", ToolCallId: "call_1"),
            ],
            Tools: [new ModelToolDefinition("list_prs", "Lists pull requests", "{\"type\":\"object\"}")],
            TurnId: "turn-7",
            MaxOutputTokens: 2048,
            Temperature: 0.2);

        var json = Serialize(request);
        var back = Deserialize<ModelRequest>(json);

        Assert.Equal("agent-charles", back.AgentId);
        Assert.Equal("custom/code", back.Route);
        Assert.Equal("turn-7", back.TurnId);
        Assert.Equal(2048, back.MaxOutputTokens);
        Assert.Equal(0.2, back.Temperature);
        Assert.Equal(4, back.Messages.Count);
        Assert.Equal(ModelRole.Tool, back.Messages[3].Role);
        Assert.Equal("call_1", back.Messages[3].ToolCallId);
        Assert.Equal("{\"state\":\"open\"}", back.Messages[2].ToolCalls![0].ArgumentsJson);
        Assert.Equal("opaque-token", back.Messages[2].ToolCalls![0].ProviderData);
        Assert.Equal("{\"type\":\"object\"}", back.Tools[0].ParametersJsonSchema);
        Assert.Equal(json, Serialize(back));
    }

    [Fact]
    public void Model_request_optional_fields_default_to_null()
    {
        var request = new ModelRequest("agent-a", "gpt-x", [new ModelMessage(ModelRole.User, "hi")], []);

        var back = Deserialize<ModelRequest>(Serialize(request));

        Assert.Null(back.TurnId);
        Assert.Null(back.MaxOutputTokens);
        Assert.Null(back.Temperature);
        Assert.Empty(back.Tools);
        Assert.Null(back.Messages[0].ToolCalls);
    }

    [Fact]
    public void Model_role_uses_snake_case_names()
    {
        var json = Serialize(new ModelMessage(ModelRole.Assistant, "ok"));

        Assert.Contains("\"assistant\"", json);
    }

    [Fact]
    public void Every_model_event_kind_round_trips_in_order()
    {
        IReadOnlyList<ModelEvent> events =
        [
            new ModelEvent.ThinkingDelta("Considering the options."),
            new ModelEvent.TextDelta("Here is "),
            new ModelEvent.TextDelta("the answer."),
            new ModelEvent.ToolCallStart("call_9", "read_file", ProviderData: "opaque-token"),
            new ModelEvent.ToolCallArgsDelta("call_9", "{\"path\":"),
            new ModelEvent.ToolCallArgsDelta("call_9", "\"a.md\"}"),
            new ModelEvent.ToolCallEnd("call_9"),
            new ModelEvent.UsageReported(new GatewayTokenUsage(1200, 300, 0, 4000, 512, 128)),
            new ModelEvent.Warning("long_prompt", "Prompt exceeds 100K tokens."),
            new ModelEvent.Failure("rate_limited", "Upstream returned 429.", RetryAfterSeconds: 30),
            new ModelEvent.Done(StopReason.ToolUse, "claude-haiku-5-5"),
        ];

        var back = Deserialize<IReadOnlyList<ModelEvent>>(Serialize(events));

        Assert.Equal(events, back);
    }

    [Fact]
    public void Model_event_kind_is_written_as_discriminator()
    {
        var json = Serialize<ModelEvent>(new ModelEvent.ToolCallEnd("call_9"));

        using var doc = JsonDocument.Parse(json);
        Assert.Equal("tool_call_end", doc.RootElement.GetProperty("kind").GetString());
        Assert.Equal("call_9", doc.RootElement.GetProperty("callId").GetString());
    }

    [Fact]
    public void Error_without_retry_after_keeps_it_null()
    {
        var back = Deserialize<ModelEvent>(Serialize<ModelEvent>(new ModelEvent.Failure("timeout", "Read timed out.")));

        var error = Assert.IsType<ModelEvent.Failure>(back);
        Assert.Null(error.RetryAfterSeconds);
    }

    [Fact]
    public void Usage_keeps_five_minute_and_one_hour_cache_writes_separate()
    {
        var usage = new GatewayTokenUsage(Input: 10, CacheWrite5m: 2000, CacheWrite1h: 500, CacheRead: 9000, Output: 300, Thinking: 40);

        var back = Deserialize<GatewayTokenUsage>(Serialize(usage));

        Assert.Equal(TokenAmount.FromInt64(2000), back.CacheWrite5m);
        Assert.Equal(TokenAmount.FromInt64(500), back.CacheWrite1h);
        Assert.Equal(usage, back);
    }

    [Fact]
    public void Usage_counts_are_written_as_exact_strings_and_survive_past_the_long_range()
    {
        var huge = TokenAmount.Parse("123456789012345678901234567890");
        var usage = new GatewayTokenUsage(huge, TokenAmount.Parse("0.5"), TokenAmount.Zero, TokenAmount.FromInt64(long.MaxValue), huge, TokenAmount.One);

        var json = Serialize(usage);

        using (var doc = JsonDocument.Parse(json))
        {
            Assert.Equal(JsonValueKind.String, doc.RootElement.GetProperty("input").ValueKind);
            Assert.Equal("123456789012345678901234567890", doc.RootElement.GetProperty("input").GetString());
            Assert.Equal("0.5", doc.RootElement.GetProperty("cacheWrite5m").GetString());
            Assert.Equal("9223372036854775807", doc.RootElement.GetProperty("cacheRead").GetString());
        }

        Assert.Equal(usage, Deserialize<GatewayTokenUsage>(json));
    }

    [Fact]
    public void Usage_counts_still_read_from_plain_json_numbers_as_older_stored_rows_have_them()
    {
        var back = Deserialize<GatewayTokenUsage>(
            """{"input":42,"cacheWrite5m":1200,"cacheWrite1h":800,"cacheRead":5000,"output":123456789012345678901234567890,"thinking":0}""");

        Assert.Equal(new GatewayTokenUsage(Input: 42, CacheWrite5m: 1200, CacheWrite1h: 800, CacheRead: 5000, Output: 0, Thinking: 0) with { Output = TokenAmount.Parse("123456789012345678901234567890") }, back);
    }

    [Theory]
    [InlineData("-1")]
    [InlineData("\"-1\"")]
    [InlineData("\"NaN\"")]
    [InlineData("\"1e999999999\"")]
    [InlineData("null")]
    [InlineData("true")]
    public void A_bad_usage_count_is_refused_not_wrapped_or_clamped(string input)
    {
        var json = $$"""{"input":{{input}},"cacheWrite5m":0,"cacheWrite1h":0,"cacheRead":0,"output":0,"thinking":0}""";

        Assert.ThrowsAny<JsonException>(() => Deserialize<GatewayTokenUsage>(json));
    }

    [Fact]
    public void A_usage_event_and_a_usage_record_round_trip_with_huge_counts()
    {
        var tokens = new GatewayTokenUsage(TokenAmount.Parse("99999999999999999999999"), TokenAmount.Zero, TokenAmount.Zero, TokenAmount.Zero, TokenAmount.Parse("77777777777777777777777"), TokenAmount.Zero);

        var back = Assert.IsType<ModelEvent.UsageReported>(Deserialize<ModelEvent>(Serialize<ModelEvent>(new ModelEvent.UsageReported(tokens))));
        Assert.Equal(tokens, back.Usage);

        var record = new GatewayUsageRecord(Guid.NewGuid(), _moment, "agent-1", null, "p", "k", "m", null, GatewayIdentity.Pooled, tokens, 5, UsageStatus.Success, null, null, false, null);
        Assert.Equal(record, Deserialize<GatewayUsageRecord>(Serialize(record)));
    }

    [Fact]
    public void Usage_record_round_trips_every_column()
    {
        var record = new GatewayUsageRecord(
            Id: Guid.Parse("3f2c9a1e-5b7d-4c8e-9a0f-1234567890ab"),
            Timestamp: _moment,
            AgentId: "agent-charles",
            TurnId: "turn-7",
            ProviderId: "anthropic-direct",
            KeyRef: "secret://bazaar/providers/anthropic-direct/api_key",
            ModelId: "claude-haiku-5-5",
            ServedModel: "claude-haiku-5-5",
            Identity: GatewayIdentity.Native,
            Tokens: new GatewayTokenUsage(100, 0, 50, 0, 20, 5),
            LatencyMs: 812,
            Status: UsageStatus.Success,
            ErrorCode: null,
            RetryAfterSeconds: null,
            Estimated: false,
            PriceVersion: "2026-10");

        var json = Serialize(record);
        var back = Deserialize<GatewayUsageRecord>(json);

        Assert.Equal(record, back);
        Assert.Contains("\"native\"", json);
        Assert.Contains("\"success\"", json);
    }

    [Fact]
    public void Aborted_usage_record_keeps_estimate_flag_and_error_code()
    {
        var record = new GatewayUsageRecord(
            Guid.NewGuid(), _moment, "agent-a", null, "freellmapi", "secret://x", "claude-sonnet-5-5", null,
            GatewayIdentity.Pooled, new GatewayTokenUsage(0, 0, 0, 0, 77, 0), 5400, UsageStatus.Aborted,
            "stream_dropped", null, Estimated: true, PriceVersion: null);

        var back = Deserialize<GatewayUsageRecord>(Serialize(record));

        Assert.Equal(UsageStatus.Aborted, back.Status);
        Assert.True(back.Estimated);
        Assert.Equal("stream_dropped", back.ErrorCode);
        Assert.Equal(GatewayIdentity.Pooled, back.Identity);
    }

    [Fact]
    public void Quota_state_uses_snake_case_names()
    {
        var json = Serialize(QuotaState.SpendCapped);

        Assert.Equal("\"spend_capped\"", json);
        Assert.Equal(QuotaState.DailyExhausted, Deserialize<QuotaState>("\"daily_exhausted\""));
    }

    [Fact]
    public void Quota_state_changed_round_trips_with_and_without_until()
    {
        var events = new GatewayQuotaEvent[]
        {
            new QuotaStateChanged("freellmapi", "secret://bazaar/providers/freellmapi/key", "claude", QuotaState.RateLimited, _moment.AddMinutes(1)),
            new QuotaStateChanged("anthropic-direct", "secret://bazaar/providers/anthropic-direct/api_key", "haiku", QuotaState.SpendCapped, null),
        };

        var back = Deserialize<IReadOnlyList<GatewayQuotaEvent>>(Serialize<IReadOnlyList<GatewayQuotaEvent>>(events));

        Assert.Equal(events, back);
    }

    [Fact]
    public void Agent_capacity_changed_round_trips_and_is_tagged()
    {
        GatewayQuotaEvent original = new AgentCapacityChanged("agent-juniper", false, _moment.AddHours(2), QuotaState.SpendCapped);

        var json = Serialize(original);
        var back = Deserialize<GatewayQuotaEvent>(json);

        Assert.Equal(original, back);
        using var doc = JsonDocument.Parse(json);
        Assert.Equal("agent_capacity_changed", doc.RootElement.GetProperty("kind").GetString());
    }
}
