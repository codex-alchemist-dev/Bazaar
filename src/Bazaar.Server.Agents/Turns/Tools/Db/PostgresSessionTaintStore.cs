using Bazaar.Server.Security.Taint;
using Npgsql;

namespace Bazaar.Server.Agents.Turns.Tools.Db;

/// <summary>
/// Postgres <see cref="ISessionTaintStore"/> over the append-only <c>session_taint</c> table (agent_turns migration 0001,
/// doc 15). <see cref="SaveAsync"/> appends a full snapshot; <see cref="LoadAsync"/> reads the latest row. A session with no
/// rows is <see cref="SessionTaint.Clean"/>. Works under the runtime role (<c>bazaar_app</c>): INSERT and SELECT only.
/// </summary>
public sealed class PostgresSessionTaintStore(NpgsqlDataSource dataSource) : ISessionTaintStore
{
    private const string _latest = "SELECT state, sources::text FROM session_taint WHERE session_id = $1 ORDER BY seq DESC LIMIT 1";

    /// <inheritdoc />
    public async Task<SessionTaint> LoadAsync(Guid sessionId, CancellationToken cancellationToken)
    {
        await using var cmd = dataSource.CreateCommand(_latest);
        cmd.Parameters.Add(new NpgsqlParameter<Guid> { TypedValue = sessionId });
        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            return SessionTaint.Clean;
        }

        return SessionTaintMapping.MapRow(reader.GetString(0), reader.GetString(1));
    }

    /// <summary>
    /// Appends the session's taint as it stands. The database rejects a level below the session's latest one.
    /// </summary>
    public async Task SaveAsync(Guid sessionId, SessionTaint taint, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(taint);

        await using var cmd = dataSource.CreateCommand(
            "INSERT INTO session_taint (session_id, state, sources) VALUES ($1, $2, CAST($3 AS jsonb))");
        cmd.Parameters.Add(new NpgsqlParameter<Guid> { TypedValue = sessionId });
        cmd.Parameters.Add(new NpgsqlParameter<string> { TypedValue = SessionTaintMapping.ToSqlState(taint.State) });
        cmd.Parameters.Add(new NpgsqlParameter<string> { TypedValue = SessionTaintMapping.SerializeSources(taint.Sources) });
        await cmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }
}
