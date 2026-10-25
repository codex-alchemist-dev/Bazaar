using System.Globalization;
using Npgsql;

namespace Bazaar.Server.Data.Migrations;

/// <summary>A migration that ran in this call.</summary>
public sealed record AppliedMigration(string Provider, int Version, string Name);

/// <summary>Outcome of <see cref="MigrationRunner.MigrateAsync"/>.</summary>
public sealed record MigrationResult(IReadOnlyList<AppliedMigration> Applied, int AlreadyApplied);

/// <summary>Where a database stands relative to the migration files in the binary.</summary>
public enum SchemaState
{
    /// <summary>Every file is applied and nothing unknown is recorded.</summary>
    Current,

    /// <summary>The history table is missing or some files are not applied yet.</summary>
    Behind,

    /// <summary>The database records a migration this binary does not have.</summary>
    Ahead,
}

/// <summary>
/// Applies embedded versioned SQL (doc 04). <c>common</c> runs first, then the host's provider folder. One transaction per
/// file (except <c>-- @no-transaction</c> files), a session advisory lock for the whole run, and a checksum guard on every
/// applied file. Forward-only. Startup never calls <see cref="MigrateAsync"/>; it calls <see cref="EnsureCurrentAsync"/>.
/// </summary>
public sealed class MigrationRunner(NpgsqlDataSource dataSource)
{
    public const string Common = "common";

    /// <summary>Advisory-lock key for the migration run. Distinct from the instance lock (doc 03).</summary>
    private const long LockKey = 0x42415A4D49475231; // "BAZMIGR1"

    /// <summary>Applies every pending file. Racing runners queue on the lock; the later one finds nothing to do.</summary>
    public async Task<MigrationResult> MigrateAsync(IMigrationSource source, string provider, CancellationToken ct = default)
    {
        var plan = Plan(source, provider);
        await using var conn = await dataSource.OpenConnectionAsync(ct).ConfigureAwait(false);
        await ExecAsync(conn, null, $"SELECT pg_advisory_lock({LockKey})", ct).ConfigureAwait(false);
        try
        {
            await ExecAsync(conn, null, CreateHistorySql, ct).ConfigureAwait(false);
            var history = await ReadHistoryAsync(conn, ct).ConfigureAwait(false);
            var pending = Diff(plan, history, throwIfAhead: true);

            var applied = new List<AppliedMigration>();
            foreach (var file in pending)
            {
                await ApplyAsync(conn, file, ct).ConfigureAwait(false);
                applied.Add(new AppliedMigration(file.Provider, file.Version, file.Name));
            }

            return new MigrationResult(applied, plan.Count - pending.Count);
        }
        finally
        {
            await ExecAsync(conn, null, $"SELECT pg_advisory_unlock({LockKey})", CancellationToken.None).ConfigureAwait(false);
        }
    }

    /// <summary>Read-only comparison of the database with the binary's files. Takes no lock and changes nothing.</summary>
    public async Task<SchemaState> GetStateAsync(IMigrationSource source, string provider, CancellationToken ct = default)
    {
        var plan = Plan(source, provider);
        await using var conn = await dataSource.OpenConnectionAsync(ct).ConfigureAwait(false);
        if (!await HistoryExistsAsync(conn, ct).ConfigureAwait(false))
            return SchemaState.Behind;
        var history = await ReadHistoryAsync(conn, ct).ConfigureAwait(false);
        if (history.Keys.Any(k => !plan.Any(f => f.Provider == k.Provider && f.Version == k.Version)))
            return SchemaState.Ahead;
        return plan.Any(f => !history.ContainsKey((f.Provider, f.Version))) ? SchemaState.Behind : SchemaState.Current;
    }

    /// <summary>Startup check (doc 03 step 4): throws unless the schema matches the binary exactly.</summary>
    public async Task EnsureCurrentAsync(IMigrationSource source, string provider, CancellationToken ct = default)
    {
        var state = await GetStateAsync(source, provider, ct).ConfigureAwait(false);
        if (state != SchemaState.Current)
            throw new SchemaVersionException(state == SchemaState.Ahead
                ? "The database schema is newer than this build; run a newer build."
                : "The database schema is older than this build; run 'migrate' (after a backup).");
    }

    private const string CreateHistorySql = """
        CREATE TABLE IF NOT EXISTS schema_migrations (
            provider   text        NOT NULL,
            version    integer     NOT NULL,
            name       text        NOT NULL,
            checksum   text        NOT NULL,
            applied_at timestamptz NOT NULL DEFAULT now(),
            PRIMARY KEY (provider, version)
        )
        """;

    private static List<MigrationFile> Plan(IMigrationSource source, string provider)
    {
        var plan = new List<MigrationFile>();
        foreach (var folder in provider == Common ? [Common] : new[] { Common, provider })
        {
            var files = source.Load(folder).OrderBy(f => f.Version).ToList();
            var duplicate = files.GroupBy(f => f.Version).FirstOrDefault(g => g.Count() > 1);
            if (duplicate is not null)
                throw new MigrationException($"Two '{folder}' migrations share version {duplicate.Key:D4}.");
            plan.AddRange(files);
        }

        return plan;
    }

    /// <summary>Checks the history against the files and returns the pending ones in apply order.</summary>
    private static List<MigrationFile> Diff(List<MigrationFile> plan, Dictionary<(string Provider, int Version), string> history, bool throwIfAhead)
    {
        foreach (var (key, checksum) in history)
        {
            var file = plan.FirstOrDefault(f => f.Provider == key.Provider && f.Version == key.Version);
            if (file is null)
            {
                if (throwIfAhead)
                    throw new SchemaVersionException(
                        $"The database records {key.Provider}/{key.Version:D4}, which this build does not have; run a newer build.");
                continue;
            }

            if (file.Checksum != checksum)
                throw new MigrationChecksumException(
                    $"Applied migration {file.Provider}/{file.Version:D4}_{file.Name} was edited after it ran (recorded {checksum[..12]}, now {file.Checksum[..12]}). Add a new file instead.");
        }

        var pending = plan.Where(f => !history.ContainsKey((f.Provider, f.Version))).ToList();
        foreach (var file in pending)
        {
            var highestApplied = history.Keys.Where(k => k.Provider == file.Provider).Select(k => k.Version).DefaultIfEmpty(0).Max();
            if (file.Version < highestApplied)
                throw new SchemaVersionException(
                    $"Migration {file.Provider}/{file.Version:D4}_{file.Name} is older than the applied {file.Provider}/{highestApplied:D4}; numbers must only grow.");
        }

        return pending;
    }

    private static async Task ApplyAsync(NpgsqlConnection conn, MigrationFile file, CancellationToken ct)
    {
        const string insert = "INSERT INTO schema_migrations (provider, version, name, checksum) VALUES ($1, $2, $3, $4)";
        NpgsqlTransaction? tx = file.NoTransaction ? null : await conn.BeginTransactionAsync(ct).ConfigureAwait(false);
        try
        {
            await ExecAsync(conn, tx, file.Sql, ct).ConfigureAwait(false);
            await using var cmd = new NpgsqlCommand(insert, conn, tx)
            {
                Parameters =
                {
                    new() { Value = file.Provider },
                    new() { Value = file.Version },
                    new() { Value = file.Name },
                    new() { Value = file.Checksum },
                },
            };
            await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
            if (tx is not null)
                await tx.CommitAsync(ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            if (tx is not null)
                await tx.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
            throw new MigrationException(
                $"Migration {file.Provider}/{file.Version:D4}_{file.Name} failed; the database stays at the last good version: {ex.Message}", ex);
        }
        finally
        {
            if (tx is not null)
                await tx.DisposeAsync().ConfigureAwait(false);
        }
    }

    private static async Task<bool> HistoryExistsAsync(NpgsqlConnection conn, CancellationToken ct)
    {
        await using var cmd = new NpgsqlCommand("SELECT to_regclass('schema_migrations') IS NOT NULL", conn);
        return (bool)(await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false))!;
    }

    private static async Task<Dictionary<(string Provider, int Version), string>> ReadHistoryAsync(NpgsqlConnection conn, CancellationToken ct)
    {
        var history = new Dictionary<(string, int), string>();
        await using var cmd = new NpgsqlCommand("SELECT provider, version, checksum FROM schema_migrations", conn);
        await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
            history[(reader.GetString(0), reader.GetInt32(1))] = reader.GetString(2);
        return history;
    }

    private static async Task ExecAsync(NpgsqlConnection conn, NpgsqlTransaction? tx, string sql, CancellationToken ct)
    {
        await using var cmd = new NpgsqlCommand(sql, conn, tx);
        await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }
}
