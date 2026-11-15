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
/// <b>Concurrency.</b> <see cref="TokenBudgetService"/> serialises its own calls in one process, so within one process a
/// limit is never exceeded. This adapter does not make the read-decide-append sequence atomic across processes. Two
/// processes can read the same total and both append, so an agent can overspend by up to the size of the racing calls.
/// The fix needs a lock that spans the read and the append (for example a per-agent advisory lock held in one transaction),
/// and <see cref="ITokenBudgetStore"/> has no transaction scope to hang it on. It is an open point, not implemented here.
/// </para>
/// </remarks>
public sealed class PostgresTokenBudgetStore(NpgsqlDataSource dataSource) : ITokenBudgetStore
{
    private const string _insert = "INSERT INTO agent_token_spend (agent_id, at, tokens) VALUES ($1, $2, $3)";
    private const string _loadSince = "SELECT at, tokens FROM agent_token_spend WHERE agent_id = $1 AND at > $2 ORDER BY at, id";
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

    /// <summary>Cuts an instant to whole microseconds, the precision Postgres stores, so a write and its read-back agree.</summary>
    private static DateTimeOffset TruncateToMicroseconds(DateTimeOffset instant)
    {
        var ticks = instant.Ticks - instant.Ticks % 10;
        return new DateTimeOffset(ticks, instant.Offset);
    }
}
