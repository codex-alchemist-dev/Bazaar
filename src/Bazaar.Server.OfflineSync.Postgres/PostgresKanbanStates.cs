using Bazaar.Server.OfflineSync.Rules;
using Npgsql;

namespace Bazaar.Server.OfflineSync.Postgres;

/// <summary>Postgres <see cref="IKanbanFieldState"/>: compare-and-set on the stored version, on the write's transaction.</summary>
public sealed class PostgresKanbanFieldState : IKanbanFieldState
{
    private const string _get = "SELECT value, version FROM offline_kanban_field WHERE task_id = $1 AND field = $2";
    private const string _insert =
        "INSERT INTO offline_kanban_field (task_id, field, value, version) VALUES ($1, $2, $3, 1) ON CONFLICT (task_id, field) DO NOTHING";
    private const string _update =
        "UPDATE offline_kanban_field SET value = $3, version = version + 1 WHERE task_id = $1 AND field = $2 AND version = $4";

    private readonly PostgresAccess _db;

    /// <summary>Creates the state over the shared data source.</summary>
    /// <param name="dataSource">The one shared data source.</param>
    public PostgresKanbanFieldState(NpgsqlDataSource dataSource)
    {
        ArgumentNullException.ThrowIfNull(dataSource);
        _db = new PostgresAccess(dataSource);
    }

    /// <inheritdoc />
    public ValueTask<bool> TryClaimWriteAsync(Guid writeId, CancellationToken cancellationToken) =>
        PostgresClaims.TryClaimAsync(_db, writeId, cancellationToken);

    /// <inheritdoc />
    public async ValueTask<KanbanFieldSnapshot> GetFieldAsync(string taskId, string field, CancellationToken cancellationToken)
    {
        var statement = await _db.PrepareAsync(_get, requireScope: false, cancellationToken).ConfigureAwait(false);
        await using (statement.ConfigureAwait(false))
        {
            statement.Add(taskId).Add(field);
            var reader = await statement.Command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            await using (reader.ConfigureAwait(false))
            {
                return await reader.ReadAsync(cancellationToken).ConfigureAwait(false)
                    ? new KanbanFieldSnapshot(reader.GetString(0), reader.GetInt64(1))
                    : new KanbanFieldSnapshot(null, 0);
            }
        }
    }

    /// <inheritdoc />
    public async ValueTask<bool> TryReplaceFieldAsync(string taskId, string field, long expectedVersion, string value, CancellationToken cancellationToken)
    {
        // A never-set field has no row (version 0), so version 0 is an insert and any other version is a guarded update.
        var statement = await _db.PrepareAsync(expectedVersion == 0 ? _insert : _update, requireScope: true, cancellationToken).ConfigureAwait(false);
        await using (statement.ConfigureAwait(false))
        {
            statement.Add(taskId).Add(field).Add(value);
            if (expectedVersion != 0)
            {
                statement.Add(expectedVersion);
            }

            return await statement.Command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) == 1;
        }
    }
}

/// <summary>
/// Postgres <see cref="IKanbanEdgeState"/>. Every edge ever added or removed keeps a row with its version; <c>present</c> says whether
/// it is in the graph. The cycle check reads the edges under the target lock of the write's scope.
/// </summary>
public sealed class PostgresKanbanEdgeState : IKanbanEdgeState
{
    private const string _edges = "SELECT from_task, to_task FROM offline_kanban_edge WHERE graph_id = $1 AND present";
    private const string _version = "SELECT version FROM offline_kanban_edge WHERE graph_id = $1 AND from_task = $2 AND to_task = $3";
    private const string _set =
        "INSERT INTO offline_kanban_edge (graph_id, from_task, to_task, present, version) VALUES ($1, $2, $3, $4, $5) " +
        "ON CONFLICT (graph_id, from_task, to_task) DO UPDATE SET present = EXCLUDED.present, version = EXCLUDED.version";

    private readonly PostgresAccess _db;

    /// <summary>Creates the state over the shared data source.</summary>
    /// <param name="dataSource">The one shared data source.</param>
    public PostgresKanbanEdgeState(NpgsqlDataSource dataSource)
    {
        ArgumentNullException.ThrowIfNull(dataSource);
        _db = new PostgresAccess(dataSource);
    }

    /// <inheritdoc />
    public ValueTask<bool> TryClaimWriteAsync(Guid writeId, CancellationToken cancellationToken) =>
        PostgresClaims.TryClaimAsync(_db, writeId, cancellationToken);

    /// <inheritdoc />
    public async ValueTask<IReadOnlyCollection<KanbanEdge>> GetEdgesAsync(string graphId, CancellationToken cancellationToken)
    {
        var statement = await _db.PrepareAsync(_edges, requireScope: false, cancellationToken).ConfigureAwait(false);
        await using (statement.ConfigureAwait(false))
        {
            statement.Add(graphId);
            var reader = await statement.Command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            await using (reader.ConfigureAwait(false))
            {
                var edges = new List<KanbanEdge>();
                while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                {
                    edges.Add(new KanbanEdge(reader.GetString(0), reader.GetString(1)));
                }

                return edges;
            }
        }
    }

    /// <inheritdoc />
    public async ValueTask<long> GetEdgeVersionAsync(string graphId, KanbanEdge edge, CancellationToken cancellationToken)
    {
        var statement = await _db.PrepareAsync(_version, requireScope: false, cancellationToken).ConfigureAwait(false);
        await using (statement.ConfigureAwait(false))
        {
            statement.Add(graphId).Add(edge.From).Add(edge.To);
            return await statement.Command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) is long version ? version : 0;
        }
    }

    /// <inheritdoc />
    public async ValueTask SetEdgeAsync(string graphId, KanbanEdge edge, bool present, long version, CancellationToken cancellationToken)
    {
        var statement = await _db.PrepareAsync(_set, requireScope: true, cancellationToken).ConfigureAwait(false);
        await using (statement.ConfigureAwait(false))
        {
            statement.Add(graphId).Add(edge.From).Add(edge.To).Add(present).Add(version);
            await statement.Command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
    }
}
