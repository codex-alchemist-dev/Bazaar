using Npgsql;

namespace Bazaar.Server.Agents.Turns.Tools.Db;

/// <summary>
/// Reads and writes the <c>in_flight_call</c> table (migration <c>turns_inflight/0001</c>, doc 06). The row is the crash
/// marker: a call begun and never completed is reported as Unknown instead of running again. Works under the runtime role
/// (<c>bazaar_app</c>): it needs INSERT, SELECT and UPDATE, never DELETE.
/// </summary>
public sealed class PostgresInFlightCallStore(NpgsqlDataSource dataSource) : IInFlightCallStore
{
    /// <inheritdoc />
    public async Task<bool> HasUnfinishedAsync(string callKey, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(callKey);

        await using var cmd = dataSource.CreateCommand("SELECT state FROM in_flight_call WHERE call_key = $1");
        cmd.Parameters.Add(new NpgsqlParameter<string> { TypedValue = callKey });
        var state = await cmd.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        return InFlightCallRows.IsUnfinished(state as string);
    }

    /// <inheritdoc />
    /// <remarks>
    /// Upserts the row as begun with a fresh lease. The lease is computed on the database clock, so every writer agrees on it.
    /// </remarks>
    public async Task BeginAsync(string callKey, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(callKey);

        await using var cmd = dataSource.CreateCommand(
            "INSERT INTO in_flight_call (call_key, state, begun_at, lease_expires_at, completed_at) " +
            "VALUES ($1, $2, now(), now() + CAST($3 AS interval), NULL) " +
            "ON CONFLICT (call_key) DO UPDATE SET state = EXCLUDED.state, begun_at = EXCLUDED.begun_at, " +
            "lease_expires_at = EXCLUDED.lease_expires_at, completed_at = NULL");
        cmd.Parameters.Add(new NpgsqlParameter<string> { TypedValue = callKey });
        cmd.Parameters.Add(new NpgsqlParameter<string> { TypedValue = InFlightCallRows.Begun });
        cmd.Parameters.Add(new NpgsqlParameter<TimeSpan> { TypedValue = InFlightCallRows.Lease });
        await cmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    /// <remarks>A key with no row is left alone, so a repeated completion is harmless.</remarks>
    public async Task CompleteAsync(string callKey, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(callKey);

        await using var cmd = dataSource.CreateCommand(
            "UPDATE in_flight_call SET state = $2, completed_at = now() WHERE call_key = $1");
        cmd.Parameters.Add(new NpgsqlParameter<string> { TypedValue = callKey });
        cmd.Parameters.Add(new NpgsqlParameter<string> { TypedValue = InFlightCallRows.Completed });
        await cmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }
}
