using Npgsql;

namespace Bazaar.Testing.Postgres;

/// <summary>
/// One migrated template database per process (per test run). Test classes clone it with
/// <c>CREATE DATABASE ... TEMPLATE</c>, which is far cheaper than re-running migrations.
/// </summary>
public sealed class PgTemplate
{
    private static readonly Lock _gate = new();
    private static readonly string _runId = Guid.NewGuid().ToString("N")[..8];
    private static readonly SemaphoreSlim _cloneLock = new(1, 1);
    private static Func<string, Task>? _migrator;
    private static Task<PgTemplate>? _shared;
    private static int _cloneCounter;

    private readonly PgSource _source;

    private PgTemplate(PgSource source, string name)
    {
        _source = source;
        Name = name;
    }

    /// <summary>Name of the template database on the server.</summary>
    public string Name { get; }

    /// <summary>
    /// Registers how the template is migrated. The delegate receives a connection string for the empty template
    /// database. Call once, before the first <see cref="PgFixture"/> initialises; the migration runner (M1)
    /// plugs in here. With none registered the template stays empty.
    /// </summary>
    public static void UseMigrator(Func<string, Task> migrate)
    {
        lock (_gate)
        {
            if (_shared is not null)
                throw new InvalidOperationException("The template database is already built; register the migrator before first use.");
            _migrator = migrate;
        }
    }

    /// <summary>Builds the process-wide template on first call.</summary>
    public static Task<PgTemplate> GetSharedAsync()
    {
        lock (_gate)
            return _shared ??= BuildAsync();
    }

    private static async Task<PgTemplate> BuildAsync()
    {
        var source = await PgSource.GetSharedAsync().ConfigureAwait(false);
        var name = $"bazaar_tpl_{_runId}";
        await ExecAdminAsync(source, $"CREATE DATABASE \"{name}\"").ConfigureAwait(false);
        var template = new PgTemplate(source, name);
        try
        {
            if (_migrator is { } migrate)
                await migrate(source.ConnectionStringFor(name, pooling: false)).ConfigureAwait(false);
        }
        catch
        {
            await ExecAdminAsync(source, $"DROP DATABASE IF EXISTS \"{name}\" WITH (FORCE)").ConfigureAwait(false);
            throw;
        }

        NpgsqlConnection.ClearAllPools();
        AppDomain.CurrentDomain.ProcessExit += (_, _) => DropAtExit(source, name);
        return template;
    }

    /// <summary>How long process exit may spend dropping the template. The test runner kills a process that is still alive 10 seconds after its run.</summary>
    private static readonly TimeSpan _exitDropBudget = TimeSpan.FromSeconds(4);

    /// <summary>
    /// Drops the template when the test process exits, within a fixed budget. On a busy server the drop can wait a long time
    /// (it needs a connection slot and a checkpoint), and an exit handler that waits for it keeps a finished test process alive
    /// until the runner force-closes it and reports a failed run with every test passed. A template that is not dropped in time
    /// is left behind; its name is unique to the run, so it cannot collide with a later one.
    /// </summary>
    private static void DropAtExit(PgSource source, string name)
    {
        try
        {
            using var cancel = new CancellationTokenSource(_exitDropBudget);
            var token = cancel.Token;
            var connectionString = new NpgsqlConnectionStringBuilder(source.AdminConnectionString)
            {
                Timeout = (int)_exitDropBudget.TotalSeconds,
                CommandTimeout = (int)_exitDropBudget.TotalSeconds,
            }.ConnectionString;
            var drop = Task.Run(() => ExecAsync(connectionString, $"DROP DATABASE IF EXISTS \"{name}\" WITH (FORCE)", token), token);
            drop.Wait(_exitDropBudget);
        }
        catch (Exception)
        {
            // The server may already be gone, or the budget ran out.
        }
    }

    /// <summary>Creates a fresh database cloned from the template and returns its name.</summary>
    internal async Task<string> CloneAsync(string label)
    {
        var name = $"bazaar_t_{_runId}_{Interlocked.Increment(ref _cloneCounter)}_{Sanitize(label)}";
        if (name.Length > 63)
            name = name[..63];

        // CREATE DATABASE ... TEMPLATE needs the template idle; serialise clones in-process and retry
        // (SQLSTATE 55006 object_in_use) in case another process still holds it.
        await _cloneLock.WaitAsync().ConfigureAwait(false);
        try
        {
            for (var attempt = 0; ; attempt++)
            {
                try
                {
                    await CreateCloneAsync(name).ConfigureAwait(false);
                    return name;
                }
                catch (PostgresException ex) when (ex.SqlState == "55006" && attempt < 20)
                {
                    await Task.Delay(100).ConfigureAwait(false);
                }
            }
        }
        finally
        {
            _cloneLock.Release();
        }
    }

    /// <summary>
    /// CREATE DATABASE ... TEMPLATE forces a checkpoint, and test processes for different projects run at the same time against
    /// one server, each with its own template. A server-wide advisory lock lets only one clone run at a time across all of them,
    /// so the checkpoints queue instead of piling up and starving every other command on the server.
    /// </summary>
    private const long _cloneAdvisoryLockKey = 0x42415A_434C4F4E; // "BAZ" "CLON"

    private async Task CreateCloneAsync(string name)
    {
        await using var conn = new NpgsqlConnection(_source.AdminConnectionString);
        await conn.OpenAsync().ConfigureAwait(false);
        await using (var lockCmd = new NpgsqlCommand("SELECT pg_advisory_lock(@k)", conn))
        {
            lockCmd.Parameters.AddWithValue("k", _cloneAdvisoryLockKey);
            await lockCmd.ExecuteNonQueryAsync().ConfigureAwait(false);
        }

        try
        {
            await using var create = new NpgsqlCommand($"CREATE DATABASE \"{name}\" TEMPLATE \"{Name}\"", conn);
            await create.ExecuteNonQueryAsync().ConfigureAwait(false);
        }
        finally
        {
            // The lock is session-level and the connection is not pooled, so closing it releases the lock even if this fails.
            await using var unlock = new NpgsqlCommand("SELECT pg_advisory_unlock(@k)", conn);
            unlock.Parameters.AddWithValue("k", _cloneAdvisoryLockKey);
            try { await unlock.ExecuteNonQueryAsync().ConfigureAwait(false); } catch (NpgsqlException) { }
        }
    }

    /// <summary>
    /// Drops a cloned database during teardown. Bounded and tolerant: a database that is still in use
    /// (55006), already gone (3D000) or whose drop was cancelled (57014) must not fail the test class,
    /// because a leftover clone is only test data and the per-run container or database is discarded.
    /// </summary>
    internal async Task DropAsync(string database)
    {
        using var budget = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        try
        {
            await ExecAsync(_source.AdminConnectionString, $"DROP DATABASE IF EXISTS \"{database}\" WITH (FORCE)", budget.Token).ConfigureAwait(false);
        }
        catch (PostgresException ex) when (ex.SqlState is "55006" or "3D000" or "57014")
        {
        }
        catch (OperationCanceledException) when (budget.IsCancellationRequested)
        {
        }
    }

    internal string ConnectionStringFor(string database) => _source.ConnectionStringFor(database);

    private static string Sanitize(string label)
    {
        var chars = label.Select(c => char.IsAsciiLetterOrDigit(c) ? char.ToLowerInvariant(c) : '_').ToArray();
        return new string(chars);
    }

    private static Task ExecAdminAsync(PgSource source, string sql) => ExecAsync(source.AdminConnectionString, sql, CancellationToken.None);

    private static async Task ExecAsync(string connectionString, string sql, CancellationToken cancellationToken)
    {
        await using var conn = new NpgsqlConnection(connectionString);
        await conn.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var cmd = new NpgsqlCommand(sql, conn);
        await cmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }
}
