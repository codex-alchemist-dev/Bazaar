using System.Text;
using Bazaar.Contracts.Numerics;
using Bazaar.Server.AgentSafety.Persistence;
using Bazaar.Server.Security.Budgets;
using Bazaar.Testing.Postgres;
using Npgsql;
using static Bazaar.Server.AgentSafety.Tests.Budgets.LargeTierTestSupport;

namespace Bazaar.Server.AgentSafety.Tests.Budgets;

/// <summary>
/// Window limits given by reference to a sealed intake amount (doc 48, DECIDED 11.4, "Limits as large amounts") against a
/// real PostgreSQL. The limit, 10^200000, has more digits than the shipped in-memory maximum, so neither it nor the window
/// sums are materialised: every window is decided by the streamed comparer.
/// </summary>
public sealed class PostgresTokenBudgetLargeLimitTests
{
    private static readonly DateTimeOffset _t0 = new(2026, 11, 19, 9, 0, 0, TimeSpan.Zero);
    private static readonly Guid _owner = Guid.Parse("00000000-0000-7000-8000-00000000000a");
    private static readonly TokenAmountBounds _wide = new(250_000, 1_000);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static void SkipWithoutDatabase() =>
        Assert.SkipWhen(PgSource.IsUnsupportedHost, "Windows without BAZAAR_TEST_PG: no test PostgreSQL available.");

    private static TokenAmount A(string text) => TokenAmount.Parse(text, _wide);

    /// <summary>10^200000 (200,001 digits), sealed through the digit-stream intake.</summary>
    private static async Task<Guid> HugeLimitAsync(PostgresLargeTokenAmountStore store)
    {
        var text = "1" + new string('0', 200_000);
        var (id, digits) = await store.IntakeAsync(new MemoryStream(Encoding.ASCII.GetBytes(text)), TokenAmountBounds.Unlimited, _owner, Ct);
        Assert.Equal(new TokenAmountDigitCounts(200_001, 0), digits);
        return id;
    }

    [Fact]
    public async Task A_limit_of_ten_to_the_200000_allows_under_denies_over_and_denies_by_digit_count_alone()
    {
        SkipWithoutDatabase();
        await using var db = await BudgetTestDb.CreateAsync(Ct);
        var budgets = new PostgresTokenBudgetStore(db.DataSource);
        var limit = await HugeLimitAsync(budgets.LargeAmounts);

        // The shipped in-memory maximum (147,455 digits) is below the limit's 200,001 digits: the limit is held only by reference.
        var limits = new TokenBudgetOptions { HourlyTokensReference = limit, DailyTokensReference = limit, WeeklyTokensReference = limit };
        limits.Validate();
        Assert.Equal(TokenBudgetOptions.DefaultInMemoryMaxDigits, limits.InMemoryMaxDigits);

        var six = await budgets.TryConsume("agent-a", _t0, A("6e199999"), limits, Ct);
        var five = await budgets.TryConsume("agent-a", _t0.AddMinutes(10), A("5e199999"), limits, Ct);
        var three = await budgets.TryConsume("agent-a", _t0.AddMinutes(20), A("3e199999"), limits, Ct);
        var exact = await budgets.TryConsume("agent-a", _t0.AddMinutes(30), A("1e199999"), limits, Ct);
        var one = await budgets.TryConsume("agent-a", _t0.AddMinutes(40), TokenAmount.FromInt64(1), limits, Ct);

        // Under the limit: allowed. The remaining count (4 x 10^199999, then 10^199999) has 200,000 digits: not materialised.
        Assert.True(six.Allowed);
        Assert.True(six.RemainingTooLarge);
        Assert.Equal(TokenAmount.Zero, six.Remaining);

        // Over it: denied in the hour window, retry unknown, nothing stored.
        Assert.False(five.Allowed);
        Assert.Equal(TokenBudgetWindow.Hour, five.Window);
        Assert.Equal(TimeSpan.MaxValue, five.RetryAfter);
        Assert.True(five.RemainingTooLarge);

        Assert.True(three.Allowed);

        // 6 + 3 + 1 (x 10^199999) is exactly the limit: allowed, with nothing left. One more token is over it.
        Assert.True(exact.Allowed);
        Assert.False(exact.RemainingTooLarge);
        Assert.Equal(TokenAmount.Zero, exact.Remaining);
        Assert.False(one.Allowed);
        Assert.Equal(TokenBudgetWindow.Hour, one.Window);
        Assert.False(one.RemainingTooLarge);

        // Three admitted spends, each a large row; plus the limit itself, which the window prune never removes.
        Assert.Equal(3L, await ScalarAsync(db.DataSource, "SELECT count(*) FROM agent_token_spend WHERE tokens_large IS NOT NULL"));
        Assert.Equal(4L, await ScalarAsync(db.DataSource, "SELECT count(*) FROM token_amount_large WHERE state = 'sealed'"));
        Assert.Equal(0L, await ScalarAsync(db.DataSource, "SELECT count(*) FROM token_amount_large WHERE state = 'writing'"));

        await budgets.PruneOlderThan(_t0.AddDays(30), Ct);
        Assert.Equal((0L, 1L, 1L), await CountsAsync(db.DataSource, Ct));
    }

    [Fact]
    public async Task A_spend_with_more_integer_digits_than_the_limit_is_denied_from_the_headers_and_never_stored()
    {
        SkipWithoutDatabase();
        await using var db = await BudgetTestDb.CreateAsync(Ct);
        var budgets = new PostgresTokenBudgetStore(db.DataSource);
        var limit = await HugeLimitAsync(budgets.LargeAmounts);
        var limits = new TokenBudgetOptions { HourlyTokensReference = limit, DailyTokensReference = limit, WeeklyTokensReference = limit };
        var before = await CountsAsync(db.DataSource, Ct);

        var decision = await budgets.TryConsume("agent-a", _t0, A("1e200001"), limits, Ct);

        Assert.False(decision.Allowed);
        Assert.Equal(TokenBudgetWindow.Hour, decision.Window);
        Assert.Equal(TimeSpan.MaxValue, decision.RetryAfter);
        Assert.Equal(TokenAmount.Zero, decision.Remaining);
        Assert.False(decision.RemainingTooLarge);
        Assert.Equal(before, await CountsAsync(db.DataSource, Ct));
    }

    [Fact]
    public async Task A_large_weekly_limit_works_beside_in_memory_hour_and_day_limits()
    {
        SkipWithoutDatabase();
        await using var db = await BudgetTestDb.CreateAsync(Ct);
        var budgets = new PostgresTokenBudgetStore(db.DataSource);
        var limit = await HugeLimitAsync(budgets.LargeAmounts);
        var limits = new TokenBudgetOptions
        {
            HourlyTokens = TokenAmount.FromInt64(1_000),
            DailyTokens = TokenAmount.FromInt64(2_000),
            WeeklyTokensReference = limit,
        };
        limits.Validate();

        var first = await budgets.TryConsume("agent-a", _t0, TokenAmount.FromInt64(600), limits, Ct);
        var second = await budgets.TryConsume("agent-a", _t0.AddMinutes(1), TokenAmount.FromInt64(500), limits, Ct);
        var later = await budgets.TryConsume("agent-a", _t0.AddHours(2), A("900.5"), limits, Ct);
        var dayFull = await budgets.TryConsume("agent-a", _t0.AddHours(3), TokenAmount.FromInt64(600), limits, Ct);

        // The tightest room is in memory, so it is reported exactly, fractions included.
        Assert.True(first.Allowed);
        Assert.Equal(TokenAmount.FromInt64(400), first.Remaining);
        Assert.False(first.RemainingTooLarge);
        Assert.False(second.Allowed);
        Assert.Equal(TokenBudgetWindow.Hour, second.Window);
        Assert.Equal(TokenAmount.FromInt64(400), second.Remaining);
        Assert.True(later.Allowed);
        Assert.Equal(A("99.5"), later.Remaining);
        Assert.False(dayFull.Allowed);
        Assert.Equal(TokenBudgetWindow.Day, dayFull.Window);
        Assert.Equal(A("499.5"), dayFull.Remaining);
    }

    [Fact]
    public async Task A_reference_that_is_not_a_sealed_intake_amount_denies_every_call()
    {
        SkipWithoutDatabase();
        await using var db = await BudgetTestDb.CreateAsync(Ct);
        var budgets = new PostgresTokenBudgetStore(db.DataSource, new TokenBudgetOptions { InMemoryMaxDigits = 300_000 });

        // A spend's own large amount: sealed, but owned by a spend row, not by an intake row.
        var spendLimits = new TokenBudgetOptions { HourlyTokens = A("1e200000"), DailyTokens = A("1e200000"), WeeklyTokens = A("1e200000"), InMemoryMaxDigits = 300_000 };
        Assert.True((await budgets.TryConsume("agent-b", _t0, A("1e150000"), spendLimits, Ct)).Allowed);
        var spendAmount = Guid.Parse((await TextAsync(db.DataSource, "SELECT tokens_large::text FROM agent_token_spend"))!);

        foreach (var reference in new[] { Guid.Parse("00000000-0000-7000-8000-0000000000ff"), spendAmount })
        {
            var limits = new TokenBudgetOptions { WeeklyTokensReference = reference };
            var decision = await budgets.TryConsume("agent-a", _t0, TokenAmount.FromInt64(1), limits, Ct);

            Assert.False(decision.Allowed);
            Assert.Equal(TimeSpan.MaxValue, decision.RetryAfter);
        }

        Assert.Equal(0L, await ScalarAsync(db.DataSource, "SELECT count(*) FROM agent_token_spend WHERE agent_id = 'agent-a'"));
    }

    [Fact]
    public async Task The_in_memory_store_denies_every_call_while_a_limit_is_a_reference()
    {
        var store = new InMemoryTokenBudgetStore();
        var limits = new TokenBudgetOptions { DailyTokensReference = Guid.Parse("00000000-0000-7000-8000-0000000000ee") };

        var decision = await store.TryConsume("agent-a", _t0, TokenAmount.FromInt64(1), limits, Ct);

        Assert.False(decision.Allowed);
        Assert.Empty(await store.LoadSince("agent-a", _t0.AddDays(-1), Ct));
    }

    private static async Task<long> ScalarAsync(NpgsqlDataSource dataSource, string sql)
    {
        await using var cmd = dataSource.CreateCommand(sql);
        return (long)(await cmd.ExecuteScalarAsync(Ct))!;
    }

    private static async Task<string?> TextAsync(NpgsqlDataSource dataSource, string sql)
    {
        await using var cmd = dataSource.CreateCommand(sql);
        return (string?)await cmd.ExecuteScalarAsync(Ct);
    }
}
