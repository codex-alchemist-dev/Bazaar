using Bazaar.OfflineSync;
using Npgsql;

namespace Bazaar.Server.OfflineSync.Postgres;

/// <summary>
/// Opens one Postgres transaction per merged write. Everything the processor and the merge rules do for the write (claim of the
/// write id, the state change, the outcome record, the merge audit) runs on that transaction and commits together (doc 56
/// section 8, item 4). The first statement takes a transaction advisory lock keyed by the write's target, so merges for one
/// target run one at a time even across several server processes (the Kanban edge cycle check relies on it).
/// </summary>
/// <param name="dataSource">The shared data source.</param>
public sealed class PostgresOfflineWriteScopeFactory(NpgsqlDataSource dataSource) : IOfflineWriteScopeFactory
{
    /// <inheritdoc />
    public IOfflineWriteScope Begin(OfflineWrite write)
    {
        ArgumentNullException.ThrowIfNull(write);
        var scope = new PostgresWriteScope(dataSource, write.Target);
        PostgresWriteScope.SetAmbient(scope);
        return scope;
    }
}

/// <summary>The transaction of one write. Found by the stores through an <see cref="AsyncLocal{T}"/> set synchronously in <c>Begin</c>.</summary>
internal sealed class PostgresWriteScope : IOfflineWriteScope
{
    private const string _lock = "SELECT pg_advisory_xact_lock(hashtextextended($1, 0))";

    private static readonly AsyncLocal<PostgresWriteScope?> _ambient = new();

    private readonly NpgsqlDataSource _dataSource;
    private readonly string _target;
    private NpgsqlConnection? _connection;
    private NpgsqlTransaction? _transaction;
    private State _state;

    public PostgresWriteScope(NpgsqlDataSource dataSource, string target)
    {
        _dataSource = dataSource;
        _target = target;
    }

    private enum State
    {
        Open,
        Committed,
        Disposed,
    }

    /// <summary>The scope of the write being merged on this logical flow, or null when none is open.</summary>
    public static PostgresWriteScope? Current => _ambient.Value is { _state: State.Open } scope ? scope : null;

    public static void SetAmbient(PostgresWriteScope scope) => _ambient.Value = scope;

    /// <summary>Opens the connection, begins the transaction and takes the target lock, once.</summary>
    public async ValueTask<(NpgsqlConnection Connection, NpgsqlTransaction Transaction)> EnsureStartedAsync(CancellationToken cancellationToken)
    {
        if (_state != State.Open)
        {
            throw new InvalidOperationException("The write scope is already completed.");
        }

        if (_connection is null || _transaction is null)
        {
            _connection = await _dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
            _transaction = await _connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
            var command = new NpgsqlCommand(_lock, _connection, _transaction);
            await using (command.ConfigureAwait(false))
            {
                command.Parameters.Add(new NpgsqlParameter<string> { TypedValue = "offline_sync:" + _target });
                await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }
        }

        return (_connection, _transaction);
    }

    public async ValueTask CommitAsync(CancellationToken cancellationToken)
    {
        if (_state != State.Open)
        {
            throw new InvalidOperationException("The write scope is already completed.");
        }

        if (_transaction is not null)
        {
            await _transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        }

        _state = State.Committed;
    }

    public async ValueTask DisposeAsync()
    {
        _state = State.Disposed;
        if (_transaction is not null)
        {
            // Disposing an uncommitted transaction rolls it back.
            await _transaction.DisposeAsync().ConfigureAwait(false);
            _transaction = null;
        }

        if (_connection is not null)
        {
            await _connection.DisposeAsync().ConfigureAwait(false);
            _connection = null;
        }
    }
}

/// <summary>Hands out commands bound to the open write scope, or to a short-lived connection when no scope is open.</summary>
internal sealed class PostgresAccess(NpgsqlDataSource dataSource)
{
    public NpgsqlDataSource DataSource { get; } = dataSource;

    /// <summary>
    /// Prepares a statement. <paramref name="requireScope"/> is true for every change that must commit with the write's other
    /// changes: it then throws when no scope is open, because a claim or a state change outside the scope could not be undone.
    /// </summary>
    public async ValueTask<Statement> PrepareAsync(string sql, bool requireScope, CancellationToken cancellationToken)
    {
        var scope = PostgresWriteScope.Current;
        if (scope is not null)
        {
            var (connection, transaction) = await scope.EnsureStartedAsync(cancellationToken).ConfigureAwait(false);
            return new Statement(new NpgsqlCommand(sql, connection, transaction), null);
        }

        if (requireScope)
        {
            throw new InvalidOperationException(
                "Offline sync merge state changes must run inside an IOfflineWriteScope (OfflineSyncProcessor opens one per write).");
        }

        var owned = await DataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        return new Statement(new NpgsqlCommand(sql, owned), owned);
    }
}

/// <summary>One command and, when it owns one, its connection.</summary>
internal sealed class Statement(NpgsqlCommand command, NpgsqlConnection? ownedConnection) : IAsyncDisposable
{
    public NpgsqlCommand Command { get; } = command;

    public Statement Add<T>(T value)
    {
        Command.Parameters.Add(new NpgsqlParameter<T> { TypedValue = value });
        return this;
    }

    public async ValueTask DisposeAsync()
    {
        await Command.DisposeAsync().ConfigureAwait(false);
        if (ownedConnection is not null)
        {
            await ownedConnection.DisposeAsync().ConfigureAwait(false);
        }
    }
}
