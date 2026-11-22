using Bazaar.Contracts.Gateway;
using Bazaar.Contracts.Numerics;
using Bazaar.Server.Gateway.Usage;
namespace Bazaar.Server.Tests.Gateway.Usage;

public class UsageRollupTests
{
    private static readonly DateTimeOffset _hour = new(2026, 10, 30, 8, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Rows_in_the_same_hour_for_the_same_key_add_up()
    {
        var records = new[]
        {
            Record(at: _hour.AddMinutes(5), input: 100, output: 10),
            Record(at: _hour.AddMinutes(59), input: 50, output: 5),
        };

        var rows = UsageRollup.Build(records, _hour, _hour.AddHours(1));

        var row = Assert.Single(rows);
        Assert.Equal(_hour, row.Hour);
        Assert.Equal(2, row.Calls);
        Assert.Equal(TokenAmount.FromInt64(150), row.Tokens.Input);
        Assert.Equal(TokenAmount.FromInt64(15), row.Tokens.Output);
    }

    [Fact]
    public void Rows_outside_the_window_are_left_out()
    {
        var records = new[]
        {
            Record(at: _hour.AddMinutes(-1), input: 1),
            Record(at: _hour.AddHours(1), input: 2),
        };

        Assert.Empty(UsageRollup.Build(records, _hour, _hour.AddHours(1)));
    }

    [Fact]
    public void Native_and_pooled_rows_stay_separate()
    {
        var records = new[]
        {
            Record(at: _hour, identity: GatewayIdentity.Native, input: 10),
            Record(at: _hour, identity: GatewayIdentity.Pooled, input: 20),
        };

        var rows = UsageRollup.Build(records, _hour, _hour.AddHours(1));

        Assert.Equal(2, rows.Count);
        Assert.Equal(TokenAmount.FromInt64(10), rows.Single(r => r.Identity == GatewayIdentity.Native).Tokens.Input);
        Assert.Equal(TokenAmount.FromInt64(20), rows.Single(r => r.Identity == GatewayIdentity.Pooled).Tokens.Input);
    }

    [Fact]
    public void Errors_and_estimates_are_counted_not_hidden()
    {
        var records = new[]
        {
            Record(at: _hour, status: UsageStatus.Error, estimated: false),
            Record(at: _hour, status: UsageStatus.Aborted, estimated: true),
            Record(at: _hour, status: UsageStatus.Success, estimated: false),
        };

        var row = Assert.Single(UsageRollup.Build(records, _hour, _hour.AddHours(1)));

        Assert.Equal(3, row.Calls);
        Assert.Equal(1, row.ErrorCalls);
        Assert.Equal(1, row.EstimatedCalls);
    }

    [Fact]
    public void A_late_abort_inside_the_window_changes_the_rebuilt_hour()
    {
        var now = _hour.AddMinutes(30);
        var records = new List<GatewayUsageRecord> { Record(at: _hour.AddMinutes(10), input: 100) };
        var first = UsageRollup.Build(records, UsageRollup.WindowStart(now), now);

        records.Add(Record(at: _hour.AddMinutes(20), status: UsageStatus.Aborted, estimated: true, input: 40));
        var rebuilt = UsageRollup.Build(records, UsageRollup.WindowStart(now), now);

        Assert.Equal(TokenAmount.FromInt64(100), first.Single().Tokens.Input);
        Assert.Equal(TokenAmount.FromInt64(140), rebuilt.Single().Tokens.Input);
        Assert.Equal(1, rebuilt.Single().EstimatedCalls);
    }

    [Fact]
    public void Window_starts_two_hours_before_the_current_hour()
    {
        var now = _hour.AddMinutes(45);

        Assert.Equal(_hour.AddHours(-2), UsageRollup.WindowStart(now));
    }

    [Fact]
    public void Floor_to_hour_drops_minutes_and_seconds()
    {
        Assert.Equal(_hour, UsageRollup.FloorToHour(_hour.AddMinutes(59).AddSeconds(59)));
    }

    [Fact]
    public void Output_is_sorted_so_reruns_are_identical()
    {
        var records = new[]
        {
            Record(at: _hour.AddHours(1), agent: "b"),
            Record(at: _hour, agent: "a"),
            Record(at: _hour, agent: "a", model: "claude-sonnet-5-5"),
        };

        var rows = UsageRollup.Build(records, _hour, _hour.AddHours(2));

        Assert.Equal(_hour, rows[0].Hour);
        Assert.Equal("claude-haiku-5-5", rows[0].ModelId);
        Assert.Equal("claude-sonnet-5-5", rows[1].ModelId);
        Assert.Equal("b", rows[2].AgentId);
    }

    private static GatewayUsageRecord Record(
        DateTimeOffset at,
        string agent = "charles",
        string model = "claude-haiku-5-5",
        GatewayIdentity identity = GatewayIdentity.Native,
        UsageStatus status = UsageStatus.Success,
        bool estimated = false,
        long input = 0,
        long output = 0) => new(
            Id: Guid.NewGuid(),
            Timestamp: at,
            AgentId: agent,
            TurnId: null,
            ProviderId: "anthropic-direct",
            KeyRef: "secret://anthropic",
            ModelId: model,
            ServedModel: null,
            Identity: identity,
            Tokens: new GatewayTokenUsage(input, 0, 0, 0, output, 0),
            LatencyMs: 1,
            Status: status,
            ErrorCode: null,
            RetryAfterSeconds: null,
            Estimated: estimated,
            PriceVersion: null);

    [Fact]
    public void Sums_past_the_long_range_stay_exact()
    {
        var huge = new GatewayTokenUsage(long.MaxValue, 0, 0, long.MaxValue, long.MaxValue, 0);
        var records = new[]
        {
            Record(at: _hour.AddMinutes(1)) with { Tokens = huge },
            Record(at: _hour.AddMinutes(2)) with { Tokens = huge },
            Record(at: _hour.AddMinutes(3)) with { Tokens = huge },
        };

        var row = Assert.Single(UsageRollup.Build(records, _hour, _hour.AddHours(1)));

        // 3 * 9223372036854775807 = 27670116110564327421: past long.MaxValue, with no wrap and no clamp.
        Assert.Equal(TokenAmount.Parse("27670116110564327421"), row.Tokens.Input);
        Assert.Equal(TokenAmount.Parse("27670116110564327421"), row.Tokens.CacheRead);
        Assert.Equal(TokenAmount.Parse("27670116110564327421"), row.Tokens.Output);
        Assert.True(row.Tokens.Input > TokenAmount.FromInt64(long.MaxValue));
    }

    [Fact]
    public void Fractional_counts_add_exactly()
    {
        var half = new GatewayTokenUsage(TokenAmount.Parse("0.1"), TokenAmount.Zero, TokenAmount.Zero, TokenAmount.Zero, TokenAmount.Parse("0.2"), TokenAmount.Zero);
        var records = new[]
        {
            Record(at: _hour.AddMinutes(1)) with { Tokens = half },
            Record(at: _hour.AddMinutes(2)) with { Tokens = half },
            Record(at: _hour.AddMinutes(3)) with { Tokens = half },
        };

        var row = Assert.Single(UsageRollup.Build(records, _hour, _hour.AddHours(1)));

        // No binary floating point: 0.1 + 0.1 + 0.1 is 0.3, not 0.30000000000000004.
        Assert.Equal(TokenAmount.Parse("0.3"), row.Tokens.Input);
        Assert.Equal(TokenAmount.Parse("0.6"), row.Tokens.Output);
    }
}
