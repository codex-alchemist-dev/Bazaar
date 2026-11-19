using Bazaar.Contracts.Numerics;
using Bazaar.Server.AgentSafety.Persistence;
using Bazaar.Server.Data.Migrations;
using Bazaar.Server.Security.Budgets;
using Bazaar.Testing.Postgres;
using Npgsql;
using static Bazaar.Server.AgentSafety.Tests.Budgets.LargeTierTestSupport;

namespace Bazaar.Server.AgentSafety.Tests.Budgets;

/// <summary>
/// Token budgets over the large tier (doc 48, DECIDED 11.4) against a real PostgreSQL: limits beyond the numeric bounds,
/// spends stored as large amounts, mixed windows, integrity failures, the header-only fast path, staged amounts, pruning and
/// the 0006 migration over existing rows.
/// </summary>
public sealed class PostgresTokenBudgetLargeTierTests
{
    private static readonly DateTimeOffset _t0 = new(2026, 11, 19, 9, 0, 0, TimeSpan.Zero);

    // Big enough for 10^200000 (200,001 digits) plus a short fraction; the shipped default is the inline tier.
    private const int _inMemoryMaxDigits = 200_001;

    private static readonly TokenAmountBounds _wide = new(250_000, 1_000);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static void SkipWithoutDatabase() =>
        Assert.SkipWhen(PgSource.IsUnsupportedHost, "Windows without BAZAAR_TEST_PG: no test PostgreSQL available.");

    private static TokenAmount A(string text) => TokenAmount.Parse(text, _wide);

    /// <summary>10^200000 in every window, beyond the numeric limits; the operator raised the in-memory maximum to hold it.</summary>
    private static TokenBudgetOptions HugeLimits()
    {
        var limit = A("1e200000");
        var options = new TokenBudgetOptions { HourlyTokens = limit, DailyTokens = limit, WeeklyTokens = limit, InMemoryMaxDigits = _inMemoryMaxDigits };
        options.Validate();
        return options;
    }

    [Fact]
    public async Task Spends_beyond_numeric_fill_a_limit_beyond_numeric_exactly_and_prune_removes_them()
    {
        SkipWithoutDatabase();
        await using var db = await BudgetTestDb.CreateAsync(Ct);
        var limits = HugeLimits();
        var store = new PostgresTokenBudgetStore(db.DataSource, limits);

        var six = await store.TryConsume("agent-a", _t0, A("6e199999"), limits, Ct);
        var five = await store.TryConsume("agent-a", _t0.AddMinutes(10), A("5e199999"), limits, Ct);
        var three = await store.TryConsume("agent-a", _t0.AddMinutes(20), A("3e199999"), limits, Ct);

        Assert.True(six.Allowed);
        Assert.Equal(A("4e199999"), six.Remaining);
        Assert.False(five.Allowed);
        Assert.Equal(TokenBudgetWindow.Hour, five.Window);
        Assert.Equal(A("4e199999"), five.Remaining);
        Assert.Equal(TimeSpan.FromMinutes(50), five.RetryAfter);
        Assert.True(three.Allowed);
        Assert.Equal(A("1e199999"), three.Remaining);

        // Both admitted spends live in the large tier: two spend rows referring to two sealed headers, one chunk each.
        Assert.Equal((2L, 2L, 2L), await CountsAsync(db.DataSource, Ct));
        Assert.Equal(2L, await ScalarAsync(db.DataSource, "SELECT count(*) FROM agent_token_spend WHERE tokens IS NULL AND tokens_large IS NOT NULL"));
        Assert.Equal(2L, await ScalarAsync(db.DataSource, "SELECT count(*) FROM token_amount_large WHERE state = 'sealed'"));
        Assert.Equal([A("6e199999"), A("3e199999")], (await store.LoadSince("agent-a", _t0.AddDays(-1), Ct)).Select(spend => spend.Tokens));

        await store.PruneOlderThan(_t0.AddMinutes(30), Ct);

        Assert.Equal((0L, 0L, 0L), await CountsAsync(db.DataSource, Ct));
    }

    [Fact]
    public async Task Inline_and_large_rows_in_one_window_are_summed_exactly()
    {
        SkipWithoutDatabase();
        await using var db = await BudgetTestDb.CreateAsync(Ct);
        var limits = HugeLimits();
        var store = new PostgresTokenBudgetStore(db.DataSource, limits);

        Assert.True((await store.TryConsume("agent-a", _t0, A("100"), limits, Ct)).Allowed);
        Assert.True((await store.TryConsume("agent-a", _t0.AddSeconds(1), A("6e199999"), limits, Ct)).Allowed);
        Assert.True((await store.TryConsume("agent-a", _t0.AddSeconds(2), A("2.5"), limits, Ct)).Allowed);

        // The rest of the limit, to the last fraction digit: 10^200000 - 6x10^199999 - 102.5.
        TokenAmount.TrySubtract(A("4e199999"), A("102.5"), out var rest);
        var exact = await store.TryConsume("agent-a", _t0.AddSeconds(3), rest, limits, Ct);
        var oneMore = await store.TryConsume("agent-a", _t0.AddSeconds(4), A("0.5"), limits, Ct);

        Assert.True(exact.Allowed);
        Assert.Equal(TokenAmount.Zero, exact.Remaining);
        Assert.False(oneMore.Allowed);
        Assert.Equal(TokenAmount.Zero, oneMore.Remaining);
        Assert.Equal((4L, 2L, 3L), await CountsAsync(db.DataSource, Ct));
    }

    [Fact]
    public async Task A_tampered_chunk_denies_the_call_and_is_reported_as_an_integrity_failure()
    {
        SkipWithoutDatabase();
        await using var db = await BudgetTestDb.CreateAsync(Ct);
        var limits = HugeLimits();
        var store = new PostgresTokenBudgetStore(db.DataSource, limits);
        Assert.True((await store.TryConsume("agent-a", _t0, A("6e199999"), limits, Ct)).Allowed);

        // 6 followed by zeros: change one zero pair to 01 in the middle of the integer chunk. Still valid base 100.
        await using (var tamper = db.DataSource.CreateCommand(
            "UPDATE token_amount_large_chunk SET digits = set_byte(digits, 5000, 1) WHERE part = 0"))
        {
            Assert.Equal(1, await tamper.ExecuteNonQueryAsync(Ct));
        }

        var decision = await store.TryConsume("agent-a", _t0.AddSeconds(1), A("1"), limits, Ct);

        Assert.False(decision.Allowed);
        Assert.Equal(TimeSpan.MaxValue, decision.RetryAfter);
        Assert.Equal(TokenAmount.Zero, decision.Remaining);
        Assert.Equal((1L, 1L, 1L), await CountsAsync(db.DataSource, Ct));
        await Assert.ThrowsAsync<TokenAmountIntegrityException>(() => store.LoadSince("agent-a", _t0.AddDays(-1), Ct));
    }

    [Fact]
    public async Task A_spend_referring_to_a_missing_large_amount_denies()
    {
        SkipWithoutDatabase();
        await using var db = await BudgetTestDb.CreateAsync(Ct);
        var limits = HugeLimits();
        var store = new PostgresTokenBudgetStore(db.DataSource, limits);
        Assert.True((await store.TryConsume("agent-a", _t0, A("6e199999"), limits, Ct)).Allowed);
        await using (var drop = db.DataSource.CreateCommand("DELETE FROM token_amount_large"))
        {
            await drop.ExecuteNonQueryAsync(Ct);
        }

        var decision = await store.TryConsume("agent-a", _t0.AddSeconds(1), A("1"), limits, Ct);

        Assert.False(decision.Allowed);
        Assert.Equal(TimeSpan.MaxValue, decision.RetryAfter);
    }

    [Fact]
    public async Task A_spend_with_more_digits_than_the_limit_is_denied_without_writing_anything()
    {
        SkipWithoutDatabase();
        await using var db = await BudgetTestDb.CreateAsync(Ct);
        var limits = HugeLimits();
        var store = new PostgresTokenBudgetStore(db.DataSource, limits);

        var decision = await store.TryConsume("agent-a", _t0, A("1e200001"), limits, Ct);

        Assert.False(decision.Allowed);
        Assert.Equal(TimeSpan.MaxValue, decision.RetryAfter);
        Assert.Equal(TokenAmount.Zero, decision.Remaining);
        Assert.Equal((0L, 0L, 0L), await CountsAsync(db.DataSource, Ct));
    }

    [Fact]
    public async Task A_staged_but_unsealed_amount_is_never_counted_and_is_swept()
    {
        SkipWithoutDatabase();
        await using var db = await BudgetTestDb.CreateAsync(Ct);
        var limits = HugeLimits();
        var store = new PostgresTokenBudgetStore(db.DataSource, limits);

        // A crash between staging and sealing leaves this: a complete header in state 'writing' that no spend refers to.
        var orphan = await store.LargeAmounts.StageAsync(A("9e199999"), Ct);
        orphan.Keep();
        await orphan.DisposeAsync();

        var first = await store.TryConsume("agent-a", _t0, A("6e199999"), limits, Ct);
        var second = await store.TryConsume("agent-a", _t0.AddSeconds(1), A("4e199999"), limits, Ct);
        var removed = await store.LargeAmounts.SweepStagedAsync(DateTimeOffset.UtcNow.AddDays(2), Ct);

        Assert.True(first.Allowed);
        Assert.True(second.Allowed);
        Assert.Equal(1, removed);
        Assert.Equal((2L, 2L, 2L), await CountsAsync(db.DataSource, Ct));
        Assert.Equal(0L, await ScalarAsync(db.DataSource, "SELECT count(*) FROM token_amount_large WHERE state = 'writing'"));
    }

    [Fact]
    public async Task Intermediate_sums_beyond_the_in_memory_maximum_are_staged_and_removed()
    {
        SkipWithoutDatabase();
        await using var db = await BudgetTestDb.CreateAsync(Ct);
        var limits = HugeLimits();
        var store = new PostgresTokenBudgetStore(db.DataSource, limits);

        // 200,000 integer digits plus one fraction digit: a sum bound of 200,002 digits, one over the in-memory maximum, so
        // the running sum is staged in the large tier while the window is added up.
        Assert.True((await store.TryConsume("agent-a", _t0, A("6e199999") + A("0.5"), limits, Ct)).Allowed);
        var next = await store.TryConsume("agent-a", _t0.AddSeconds(1), A("3e199999"), limits, Ct);

        Assert.True(next.Allowed);
        TokenAmount.TrySubtract(A("1e199999"), A("0.5"), out var remaining);
        Assert.Equal(remaining, next.Remaining);
        Assert.Equal((2L, 2L, 3L), await CountsAsync(db.DataSource, Ct));
        Assert.Equal(0L, await ScalarAsync(db.DataSource, "SELECT count(*) FROM token_amount_large WHERE state = 'writing'"));
    }

    [Fact]
    public async Task A_window_sum_beyond_the_in_memory_maximum_denies()
    {
        SkipWithoutDatabase();
        await using var db = await BudgetTestDb.CreateAsync(Ct);
        var appended = HugeLimits();
        var store = new PostgresTokenBudgetStore(db.DataSource, appended);
        await store.Append("agent-a", _t0, A("6e199999"), Ct);

        // The same limit, but now the operator lowered the in-memory maximum below the window sum's size.
        var limits = HugeLimits();
        limits.InMemoryMaxDigits = 150_000;
        var decision = await store.TryConsume("agent-a", _t0.AddSeconds(1), A("1"), limits, Ct);

        Assert.False(decision.Allowed);
        Assert.Equal(TimeSpan.MaxValue, decision.RetryAfter);
        Assert.Equal((1L, 1L, 1L), await CountsAsync(db.DataSource, Ct));
    }

    [Fact]
    public async Task Racers_with_large_spends_are_serialised_by_the_agent_lock()
    {
        SkipWithoutDatabase();
        await using var db = await BudgetTestDb.CreateAsync(Ct);
        var limits = HugeLimits();
        var store = new PostgresTokenBudgetStore(db.DataSource, limits);

        // Each spend is 3x10^199999: at most three fit 10^200000, whatever the interleaving.
        var decisions = await Task.WhenAll(Enumerable.Range(0, 8).Select(i =>
            store.TryConsume("agent-a", _t0.AddMilliseconds(i), A("3e199999"), limits, Ct)));

        Assert.Equal(3, decisions.Count(decision => decision.Allowed));
        Assert.Equal((3L, 3L, 3L), await CountsAsync(db.DataSource, Ct));
    }

    [Fact]
    public async Task Migration_0006_applies_over_0005_with_existing_rows_intact()
    {
        SkipWithoutDatabase();
        var server = new EmbeddedMigrationSource(typeof(MigrationRunner).Assembly);
        await using var db = await PgTestDatabase.CreateAsync(
            "budget_m6",
            (dataSource, ct) => new MigrationRunner(dataSource).MigrateAsync(
                new WithoutMigration(server, "agent_safety", 6), MigrateCommand.CurrentProvider(), ct),
            Ct);
        await using (var before = db.DataSource.CreateCommand(
            "INSERT INTO agent_token_spend (agent_id, at, tokens) VALUES ('agent-a', $1, 120), ('agent-b', $1, 0.25)"))
        {
            before.Parameters.AddWithValue(_t0);
            await before.ExecuteNonQueryAsync(Ct);
        }

        var result = await new MigrationRunner(db.DataSource).MigrateAsync(server, MigrateCommand.CurrentProvider(), Ct);

        var applied = Assert.Single(result.Applied);
        Assert.Equal(("agent_safety", 6), (applied.Namespace, applied.Version));
        var store = new PostgresTokenBudgetStore(db.DataSource);
        Assert.Equal(TokenAmount.FromInt64(120), Assert.Single(await store.LoadSince("agent-a", _t0.AddDays(-1), Ct)).Tokens);
        Assert.Equal(TokenAmount.Parse("0.25"), Assert.Single(await store.LoadSince("agent-b", _t0.AddDays(-1), Ct)).Tokens);

        // Exactly one of tokens and tokens_large, and tokens > 0 still holds.
        foreach (var bad in new[]
        {
            "INSERT INTO agent_token_spend (agent_id, at) VALUES ('agent-a', now())",
            "INSERT INTO agent_token_spend (agent_id, at, tokens, tokens_large) VALUES ('agent-a', now(), 1, uuidv7())",
            "INSERT INTO agent_token_spend (agent_id, at, tokens) VALUES ('agent-a', now(), 0)",
            "INSERT INTO token_amount_large (state) VALUES ('sealed')",
            "INSERT INTO token_amount_large_chunk (amount_id, part, seq, digits) VALUES (uuidv7(), 2, 0, '\\x01')",
            "INSERT INTO token_amount_large_chunk (amount_id, part, seq, digits) VALUES (uuidv7(), 0, -1, '\\x01')",
            "INSERT INTO token_amount_large_chunk (amount_id, part, seq, digits) VALUES (uuidv7(), 0, 0, '\\x')",
        })
        {
            await using var insert = db.DataSource.CreateCommand(bad);
            var error = await Assert.ThrowsAsync<PostgresException>(() => insert.ExecuteNonQueryAsync(Ct));
            Assert.Equal(PostgresErrorCodes.CheckViolation, error.SqlState);
        }
    }

    private static async Task<long> ScalarAsync(NpgsqlDataSource dataSource, string sql)
    {
        await using var cmd = dataSource.CreateCommand(sql);
        return (long)(await cmd.ExecuteScalarAsync(Ct))!;
    }
}
