using Bazaar.Contracts.Numerics;
using Npgsql;
using NpgsqlTypes;

namespace Bazaar.Server.Security.Budgets.Postgres;

/// <summary>
/// Postgres <see cref="ITokenBudgetStore"/> over the <c>agent_token_spend</c> table (agent_safety migrations 0001 and 0005).
/// Each spend is one row. Every instant is the caller's instant, never the database clock, so the service's
/// <see cref="TimeProvider"/> stays the only source of time. Works under the runtime role (<c>bazaar_app</c>): SELECT,
/// INSERT and DELETE only.
/// </summary>
/// <remarks>
/// Instants are stored at microsecond precision, the precision Postgres keeps, so <see cref="Append"/> cuts them to
/// microseconds before writing. A zero-token spend is not written: it counts for nothing, and the table requires
/// <c>tokens &gt; 0</c>.
/// <para>
/// <b>Amounts.</b> <c>tokens</c> is unconstrained <c>numeric</c> (doc 48, DECIDED 11.3). Amounts are sent as text cast to
/// <c>numeric</c> and read back, sums included, as <c>::text</c>, then parsed with <see cref="TokenAmount"/>: the driver's
/// <see cref="decimal"/> and <see cref="System.Numerics.BigInteger"/> mappings cannot carry every scale. An amount outside
/// <see cref="TokenAmountBounds.PostgresNumeric"/> cannot be stored: <see cref="TryConsume"/> denies it and
/// <see cref="Append"/> refuses it, and neither truncates. A window sum that Postgres cannot compute (numeric overflow) or
/// that does not parse within those bounds counts as over every limit, so the call is denied, never allowed and never
/// thrown to the caller.
/// </para>
/// <para>
/// <b>Concurrency.</b> <see cref="TryConsume"/> runs in one transaction: it takes a per-agent transaction-scoped advisory
/// lock (<c>pg_advisory_xact_lock(hashtext(agent))</c>) first, sums the agent's spends for the three window starts in one
/// query, inserts only if the spend fits all three, then commits. A denial rolls back and writes nothing. Calls for one
/// agent from any number of processes queue on the lock, so admitted spend never exceeds a window; calls for other agents
/// do not wait (a <c>hashtext</c> collision between two agents only makes them share a lock, never weakens it).
/// </para>
/// </remarks>
public sealed class PostgresTokenBudgetStore(NpgsqlDataSource dataSource) : ITokenBudgetStore
{
    private const string _insert = "INSERT INTO agent_token_spend (agent_id, at, tokens) VALUES ($1, $2, $3::numeric)";
    private const string _loadSince = "SELECT at, tokens::text FROM agent_token_spend WHERE agent_id = $1 AND at > $2 ORDER BY at, id";
    private const string _lock = "SELECT pg_advisory_xact_lock(hashtext($1))";
    private const string _sums =
        "SELECT coalesce(sum(tokens) FILTER (WHERE at > $2), 0)::text, " +
        "coalesce(sum(tokens) FILTER (WHERE at > $3), 0)::text, " +
        "coalesce(sum(tokens), 0)::text " +
        "FROM agent_token_spend WHERE agent_id = $1 AND at > $4";
    private const string _prune = "DELETE FROM agent_token_spend WHERE at <= $1";

    /// <inheritdoc />
    /// <exception cref="ArgumentException"><paramref name="agentId"/> is null, empty or whitespace.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="tokens"/> does not fit <see cref="TokenAmountBounds.PostgresNumeric"/>.</exception>
    public async Task Append(string agentId, DateTimeOffset at, TokenAmount tokens, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(agentId);
        if (!tokens.Fits(TokenAmountBounds.PostgresNumeric))
        {
            throw new ArgumentOutOfRangeException(nameof(tokens), "The amount has more digits than a numeric column can store.");
        }
        ct.ThrowIfCancellationRequested();

        if (tokens.IsZero)
        {
            return;
        }

        await using var cmd = dataSource.CreateCommand(_insert);
        cmd.Parameters.Add(new NpgsqlParameter<string> { TypedValue = agentId });
        cmd.Parameters.Add(new NpgsqlParameter<DateTimeOffset> { TypedValue = TruncateToMicroseconds(at) });
        cmd.Parameters.Add(AmountParameter(tokens));
        await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    /// <inheritdoc />
    /// <exception cref="ArgumentException"><paramref name="agentId"/> is null, empty or whitespace.</exception>
    public async Task<IReadOnlyList<TokenBudgetSpend>> LoadSince(string agentId, DateTimeOffset since, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(agentId);
        ct.ThrowIfCancellationRequested();

        await using var cmd = dataSource.CreateCommand(_loadSince);
        cmd.Parameters.Add(new NpgsqlParameter<string> { TypedValue = agentId });
        cmd.Parameters.Add(new NpgsqlParameter<DateTimeOffset> { TypedValue = TruncateToMicroseconds(since) });
        await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);

        var spends = new List<TokenBudgetSpend>();
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            spends.Add(new TokenBudgetSpend(reader.GetFieldValue<DateTimeOffset>(0), ReadStoredAmount(reader, 1)));
        }
        return spends;
    }

    /// <inheritdoc />
    public async Task PruneOlderThan(DateTimeOffset instant, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();

        await using var cmd = dataSource.CreateCommand(_prune);
        cmd.Parameters.Add(new NpgsqlParameter<DateTimeOffset> { TypedValue = TruncateToMicroseconds(instant) });
        await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    /// <inheritdoc />
    /// <exception cref="ArgumentException"><paramref name="agentId"/> is null, empty or whitespace.</exception>
    /// <exception cref="ArgumentNullException"><paramref name="limits"/> is null.</exception>
    public async Task<TokenBudgetDecision> TryConsume(string agentId, DateTimeOffset at, TokenAmount tokens, TokenBudgetOptions limits, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(agentId);
        ArgumentNullException.ThrowIfNull(limits);
        ct.ThrowIfCancellationRequested();

        // Before any database work: more integer digits than a limit can never fit, whatever has been spent.
        if (TokenBudgetEvaluator.RejectOversized(tokens, limits) is { } oversized)
        {
            return oversized;
        }

        // An amount the numeric column cannot hold is denied, never truncated. Only a limit set beyond the numeric bounds
        // lets such an amount past the check above.
        if (!tokens.Fits(TokenAmountBounds.PostgresNumeric))
        {
            return TokenBudgetEvaluator.Unrepresentable();
        }

        // Window starts are computed from the truncated instant, so what is summed and what is inserted agree to the microsecond.
        var now = TruncateToMicroseconds(at);

        await using var conn = await dataSource.OpenConnectionAsync(ct).ConfigureAwait(false);
        await using var tx = await conn.BeginTransactionAsync(ct).ConfigureAwait(false);

        // 1. Serialise every consumer of this agent until commit or rollback.
        await using (var lockCmd = new NpgsqlCommand(_lock, conn, tx))
        {
            lockCmd.Parameters.Add(new NpgsqlParameter<string> { TypedValue = agentId });
            await lockCmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }

        // 2. One query: tokens used in the hour, day and week windows, as exact text.
        TokenAmount? hourUsed, dayUsed, weekUsed;
        try
        {
            await using var sumCmd = new NpgsqlCommand(_sums, conn, tx);
            sumCmd.Parameters.Add(new NpgsqlParameter<string> { TypedValue = agentId });
            sumCmd.Parameters.Add(new NpgsqlParameter<DateTimeOffset> { TypedValue = TokenBudgetEvaluator.StartOf(TokenBudgetWindow.Hour, now) });
            sumCmd.Parameters.Add(new NpgsqlParameter<DateTimeOffset> { TypedValue = TokenBudgetEvaluator.StartOf(TokenBudgetWindow.Day, now) });
            sumCmd.Parameters.Add(new NpgsqlParameter<DateTimeOffset> { TypedValue = TokenBudgetEvaluator.StartOf(TokenBudgetWindow.Week, now) });
            await using var reader = await sumCmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
            await reader.ReadAsync(ct).ConfigureAwait(false);
            hourUsed = ReadSum(reader, 0);
            dayUsed = ReadSum(reader, 1);
            weekUsed = ReadSum(reader, 2);
        }
        catch (PostgresException ex) when (ex.SqlState == PostgresErrorCodes.NumericValueOutOfRange)
        {
            // The sum is beyond what numeric can hold (only spends written around TryConsume can get there): over every
            // limit. The transaction is aborted, so roll back and deny.
            await tx.RollbackAsync(ct).ConfigureAwait(false);
            return TokenBudgetEvaluator.Unrepresentable();
        }

        var decision = TokenBudgetEvaluator.Decide(hourUsed, dayUsed, weekUsed, tokens, limits, out var needed, out var neverFits);

        if (!decision.Allowed)
        {
            // Denied: read the week's spends (still under the lock) only to work out the retry time, then roll back. When
            // the retry time is already known (never fits, or a sum was not representable) nothing more is read.
            var retryAfter = TimeSpan.MaxValue;
            if (!neverFits && needed is not null)
            {
                var spends = await LoadSinceInTransaction(conn, tx, agentId, TokenBudgetEvaluator.WidestStart(now), ct).ConfigureAwait(false);
                retryAfter = TokenBudgetEvaluator.RetryAfter(spends, now, decision.Window!.Value, needed, neverFits);
            }
            await tx.RollbackAsync(ct).ConfigureAwait(false);
            return decision with { RetryAfter = retryAfter };
        }

        // 3. It fits: record it, then 4. commit, which also releases the lock.
        if (!tokens.IsZero)
        {
            await using var insertCmd = new NpgsqlCommand(_insert, conn, tx);
            insertCmd.Parameters.Add(new NpgsqlParameter<string> { TypedValue = agentId });
            insertCmd.Parameters.Add(new NpgsqlParameter<DateTimeOffset> { TypedValue = now });
            insertCmd.Parameters.Add(AmountParameter(tokens));
            await insertCmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }
        await tx.CommitAsync(ct).ConfigureAwait(false);
        return decision;
    }

    private static async Task<IReadOnlyList<TokenBudgetSpend>> LoadSinceInTransaction(
        NpgsqlConnection conn, NpgsqlTransaction tx, string agentId, DateTimeOffset since, CancellationToken ct)
    {
        await using var cmd = new NpgsqlCommand(_loadSince, conn, tx);
        cmd.Parameters.Add(new NpgsqlParameter<string> { TypedValue = agentId });
        cmd.Parameters.Add(new NpgsqlParameter<DateTimeOffset> { TypedValue = since });
        await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);

        var spends = new List<TokenBudgetSpend>();
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            spends.Add(new TokenBudgetSpend(reader.GetFieldValue<DateTimeOffset>(0), ReadStoredAmount(reader, 1)));
        }
        return spends;
    }

    /// <summary>An amount as a text parameter; the SQL casts it to <c>numeric</c>.</summary>
    private static NpgsqlParameter<string> AmountParameter(TokenAmount tokens) =>
        new() { TypedValue = tokens.ToString(), NpgsqlDbType = NpgsqlDbType.Text };

    /// <summary>A window sum read as text: null (over every limit) when it does not parse within the numeric bounds.</summary>
    private static TokenAmount? ReadSum(NpgsqlDataReader reader, int ordinal) =>
        TokenAmount.TryParse(reader.GetString(ordinal), out var sum) ? sum : null;

    /// <summary>One stored spend read as text. The column's CHECK and the numeric limits mean it always parses.</summary>
    private static TokenAmount ReadStoredAmount(NpgsqlDataReader reader, int ordinal) => TokenAmount.Parse(reader.GetString(ordinal));

    /// <summary>Cuts an instant to whole microseconds, the precision Postgres stores, so a write and its read-back agree.</summary>
    private static DateTimeOffset TruncateToMicroseconds(DateTimeOffset instant)
    {
        var ticks = instant.Ticks - instant.Ticks % 10;
        return new DateTimeOffset(ticks, instant.Offset);
    }
}
