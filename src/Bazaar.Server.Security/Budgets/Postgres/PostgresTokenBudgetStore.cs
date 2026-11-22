using Npgsql;

namespace Bazaar.Server.Security.Budgets.Postgres;

/// <summary>
/// Postgres <see cref="ITokenBudgetStore"/> over the <c>agent_token_spend</c> table (agent_safety migration 0001). Each spend
/// is one row. Every instant is the caller's instant, never the database clock, so the service's <see cref="TimeProvider"/>
/// stays the only source of time. Works under the runtime role (<c>bazaar_app</c>): SELECT, INSERT and DELETE only.
/// </summary>
/// <remarks>
/// Instants are stored at microsecond precision, the precision Postgres keeps, so <see cref="Append"/> cuts them to
/// microseconds before writing. A zero-token spend is not written: it counts for nothing, and the table requires
/// <c>tokens &gt; 0</c>.
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
    private const string _insert = "INSERT INTO agent_token_spend (agent_id, at, tokens) VALUES ($1, $2, $3)";
    private const string _loadSince = "SELECT at, tokens FROM agent_token_spend WHERE agent_id = $1 AND at > $2 ORDER BY at, id";
    private const string _lock = "SELECT pg_advisory_xact_lock(hashtext($1))";
    private const string _sums =
        "SELECT coalesce(sum(tokens) FILTER (WHERE at > $2), 0)::bigint, " +
        "coalesce(sum(tokens) FILTER (WHERE at > $3), 0)::bigint, " +
        "coalesce(sum(tokens), 0)::bigint " +
        "FROM agent_token_spend WHERE agent_id = $1 AND at > $4";
    private const string _prune = "DELETE FROM agent_token_spend WHERE at <= $1";

    /// <inheritdoc />
    /// <exception cref="ArgumentException"><paramref name="agentId"/> is null, empty or whitespace.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="tokens"/> is negative.</exception>
    public async Task Append(string agentId, DateTimeOffset at, long tokens, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(agentId);
        ArgumentOutOfRangeException.ThrowIfNegative(tokens);
        ct.ThrowIfCancellationRequested();

        if (tokens == 0)
        {
            return;
        }

        await using var cmd = dataSource.CreateCommand(_insert);
        cmd.Parameters.Add(new NpgsqlParameter<string> { TypedValue = agentId });
        cmd.Parameters.Add(new NpgsqlParameter<DateTimeOffset> { TypedValue = TruncateToMicroseconds(at) });
        cmd.Parameters.Add(new NpgsqlParameter<long> { TypedValue = tokens });
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
            spends.Add(new TokenBudgetSpend(reader.GetFieldValue<DateTimeOffset>(0), reader.GetInt64(1)));
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
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="tokens"/> is negative.</exception>
    /// <exception cref="ArgumentNullException"><paramref name="limits"/> is null.</exception>
    public async Task<TokenBudgetDecision> TryConsume(string agentId, DateTimeOffset at, long tokens, TokenBudgetOptions limits, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(agentId);
        ArgumentOutOfRangeException.ThrowIfNegative(tokens);
        ArgumentNullException.ThrowIfNull(limits);
        ct.ThrowIfCancellationRequested();

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

        // 2. One query: tokens used in the hour, day and week windows.
        long hourUsed, dayUsed, weekUsed;
        await using (var sumCmd = new NpgsqlCommand(_sums, conn, tx))
        {
            sumCmd.Parameters.Add(new NpgsqlParameter<string> { TypedValue = agentId });
            sumCmd.Parameters.Add(new NpgsqlParameter<DateTimeOffset> { TypedValue = TokenBudgetEvaluator.StartOf(TokenBudgetWindow.Hour, now) });
            sumCmd.Parameters.Add(new NpgsqlParameter<DateTimeOffset> { TypedValue = TokenBudgetEvaluator.StartOf(TokenBudgetWindow.Day, now) });
            sumCmd.Parameters.Add(new NpgsqlParameter<DateTimeOffset> { TypedValue = TokenBudgetEvaluator.StartOf(TokenBudgetWindow.Week, now) });
            await using var reader = await sumCmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
            await reader.ReadAsync(ct).ConfigureAwait(false);
            hourUsed = reader.GetInt64(0);
            dayUsed = reader.GetInt64(1);
            weekUsed = reader.GetInt64(2);
        }

        var decision = TokenBudgetEvaluator.Decide(hourUsed, dayUsed, weekUsed, tokens, limits, out var needed, out var neverFits);

        if (!decision.Allowed)
        {
            // Denied: read the week's spends (still under the lock) only to work out the retry time, then roll back.
            var spends = await LoadSinceInTransaction(conn, tx, agentId, TokenBudgetEvaluator.WidestStart(now), ct).ConfigureAwait(false);
            await tx.RollbackAsync(ct).ConfigureAwait(false);
            return decision with { RetryAfter = TokenBudgetEvaluator.RetryAfter(spends, now, decision.Window!.Value, needed, neverFits) };
        }

        // 3. It fits: record it, then 4. commit, which also releases the lock.
        if (tokens > 0)
        {
            await using var insertCmd = new NpgsqlCommand(_insert, conn, tx);
            insertCmd.Parameters.Add(new NpgsqlParameter<string> { TypedValue = agentId });
            insertCmd.Parameters.Add(new NpgsqlParameter<DateTimeOffset> { TypedValue = now });
            insertCmd.Parameters.Add(new NpgsqlParameter<long> { TypedValue = tokens });
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
            spends.Add(new TokenBudgetSpend(reader.GetFieldValue<DateTimeOffset>(0), reader.GetInt64(1)));
        }
        return spends;
    }

    /// <summary>Cuts an instant to whole microseconds, the precision Postgres stores, so a write and its read-back agree.</summary>
    private static DateTimeOffset TruncateToMicroseconds(DateTimeOffset instant)
    {
        var ticks = instant.Ticks - instant.Ticks % 10;
        return new DateTimeOffset(ticks, instant.Offset);
    }
}
