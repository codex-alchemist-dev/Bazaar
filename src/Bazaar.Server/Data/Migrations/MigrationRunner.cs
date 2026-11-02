using System.Globalization;
using Bazaar.Server.Components;
using Npgsql;

namespace Bazaar.Server.Data.Migrations;

/// <summary>A migration that ran in this call.</summary>
public sealed record AppliedMigration(string Namespace, int Version, string Name);

/// <summary>Outcome of <see cref="MigrationRunner.MigrateAsync"/>.</summary>
public sealed record MigrationResult(IReadOnlyList<AppliedMigration> Applied, int AlreadyApplied);

/// <summary>
/// Values migrations read at apply time through <c>current_setting('bazaar.*')</c>. Fixed once the first migration ran;
/// a different value on a later run is refused (doc 04).
/// </summary>
/// <param name="PartitionCount">Hash partitions for the GC log tables (<c>gc.partition_count</c>, default 16 from spike S3).</param>
public sealed record MigrationOptions(int PartitionCount = 16);

/// <summary>Where a database stands relative to the migration files in the binary.</summary>
public enum SchemaState
{
    /// <summary>Every file is applied and nothing unknown is recorded.</summary>
    Current,

    /// <summary>The history table is missing, predates namespaces, or some files are not applied yet.</summary>
    Behind,

    /// <summary>The database records a migration this binary does not have.</summary>
    Ahead,
}

/// <summary>
/// Applies embedded versioned SQL (doc 04, components design section 5). Namespaces run in the order
/// <see cref="MigrationNamespaces.Order"/> gives: <c>common</c> first, then component namespaces in dependency order, then the rest.
/// History is keyed by <c>(namespace, version)</c>. One transaction per file (except <c>-- @no-transaction</c> files), a session
/// advisory lock for the whole run, and a checksum guard on every applied file. Forward-only. Startup never calls
/// <see cref="MigrateAsync"/>; it calls <see cref="EnsureCurrentAsync"/>.
/// </summary>
public sealed class MigrationRunner(NpgsqlDataSource dataSource, MigrationOptions? options = null, IEnumerable<ComponentManifest>? components = null)
{
    private readonly MigrationOptions _options = options ?? new MigrationOptions();
    private readonly List<ComponentManifest> _components = [.. components ?? []];

    public const string Common = MigrationNamespaces.Common;

    /// <summary>Advisory-lock key for the migration run. Distinct from the instance lock (doc 03).</summary>
    private const long _lockKey = 0x42415A4D49475231; // "BAZMIGR1"

    /// <summary>Applies every pending file. Racing runners queue on the lock; the later one finds nothing to do.</summary>
    public async Task<MigrationResult> MigrateAsync(IMigrationSource source, string provider, CancellationToken ct = default)
    {
        var plan = Plan(source, provider, _components);
        await using var conn = await dataSource.OpenConnectionAsync(ct).ConfigureAwait(false);
        await ExecAsync(conn, null, $"SELECT pg_advisory_lock({_lockKey})", ct).ConfigureAwait(false);
        try
        {
            await ExecAsync(conn, null, _createHistorySql, ct).ConfigureAwait(false);
            await ExecAsync(conn, null, _historyUpgradeSql, ct).ConfigureAwait(false);
            var history = await ReadHistoryAsync(conn, ct).ConfigureAwait(false);
            var pending = Diff(plan, history, throwIfAhead: true);
            await CheckFixedSettingsAsync(conn, ct).ConfigureAwait(false);
            await SetSessionOptionAsync(conn, "bazaar.partition_count", _options.PartitionCount.ToString(CultureInfo.InvariantCulture), ct).ConfigureAwait(false);

            var applied = new List<AppliedMigration>();
            foreach (var file in pending)
            {
                await ApplyAsync(conn, file, ct).ConfigureAwait(false);
                applied.Add(new AppliedMigration(file.Namespace, file.Version, file.Name));
            }

            return new MigrationResult(applied, plan.Count - pending.Count);
        }
        finally
        {
            await SetSessionOptionAsync(conn, "bazaar.partition_count", "", CancellationToken.None).ConfigureAwait(false);
            await ExecAsync(conn, null, $"SELECT pg_advisory_unlock({_lockKey})", CancellationToken.None).ConfigureAwait(false);
        }
    }

    /// <summary>Read-only comparison of the database with the binary's files. Takes no lock and changes nothing.</summary>
    public async Task<SchemaState> GetStateAsync(IMigrationSource source, string provider, CancellationToken ct = default)
    {
        var plan = Plan(source, provider, _components);
        await using var conn = await dataSource.OpenConnectionAsync(ct).ConfigureAwait(false);
        if (!await HistoryExistsAsync(conn, ct).ConfigureAwait(false))
            return SchemaState.Behind;
        // A history table from before namespaces is upgraded by MigrateAsync, never here (this method changes nothing).
        if (!await HistoryHasNamespaceAsync(conn, ct).ConfigureAwait(false))
            return SchemaState.Behind;
        var history = await ReadHistoryAsync(conn, ct).ConfigureAwait(false);
        if (history.Keys.Any(k => !plan.Any(f => f.Namespace == k.Namespace && f.Version == k.Version)))
            return SchemaState.Ahead;
        return plan.Any(f => !history.ContainsKey((f.Namespace, f.Version))) ? SchemaState.Behind : SchemaState.Current;
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

    /// <summary>
    /// The files this run applies, in apply order, for the given host provider. Includes every namespace the source holds except the
    /// provider folders of other hosts. Pure: needs no database, so tests can check discovery and ordering on their own.
    /// </summary>
    /// <exception cref="MigrationException">A namespace name is invalid, or a namespace holds two files with one version.</exception>
    public static IReadOnlyList<MigrationFile> Plan(IMigrationSource source, string provider, IEnumerable<ComponentManifest> components)
    {
        var discovered = source.Namespaces();
        var included = discovered
            .Where(ns => !MigrationNamespaces.ProviderFolders.Contains(ns) || (provider != Common && ns == provider))
            .ToList();

        var plan = new List<MigrationFile>();
        foreach (var ns in MigrationNamespaces.Order(included, components))
        {
            var files = source.Load(ns).OrderBy(f => f.Version).ToList();
            var duplicate = files.GroupBy(f => f.Version).FirstOrDefault(g => g.Count() > 1);
            if (duplicate is not null)
                throw new MigrationException($"Two '{ns}' migrations share version {duplicate.Key:D4}.");
            plan.AddRange(files);
        }

        return plan;
    }

    private const string _createHistorySql = """
        CREATE TABLE IF NOT EXISTS schema_migrations (
            provider   text        NOT NULL,
            namespace  text        NOT NULL DEFAULT 'common',
            version    integer     NOT NULL,
            name       text        NOT NULL,
            checksum   text        NOT NULL,
            applied_at timestamptz NOT NULL DEFAULT now(),
            PRIMARY KEY (namespace, version)
        )
        """;

    /// <summary>
    /// Additive, idempotent upgrade of the history table to namespace keys. Existing rows keep their version, name and checksum;
    /// they become namespace <c>common</c>, or their provider folder when that is not <c>common</c>. <c>provider</c> stays
    /// (written equal to the namespace) so older builds still read the table. Safe to run on every migrate.
    /// </summary>
    private const string _historyUpgradeSql = """
        ALTER TABLE schema_migrations ADD COLUMN IF NOT EXISTS namespace text NOT NULL DEFAULT 'common';
        UPDATE schema_migrations SET namespace = provider WHERE namespace = 'common' AND provider <> 'common';
        DO $$
        BEGIN
            IF NOT EXISTS (
                SELECT 1 FROM pg_constraint
                WHERE conrelid = 'schema_migrations'::regclass AND contype = 'p'
                  AND pg_get_constraintdef(oid) = 'PRIMARY KEY (namespace, version)') THEN
                ALTER TABLE schema_migrations DROP CONSTRAINT schema_migrations_pkey;
                ALTER TABLE schema_migrations ADD CONSTRAINT schema_migrations_pkey PRIMARY KEY (namespace, version);
            END IF;
        END
        $$
        """;

    /// <summary>Checks the history against the files and returns the pending ones in apply order.</summary>
    private static List<MigrationFile> Diff(IReadOnlyList<MigrationFile> plan, Dictionary<(string Namespace, int Version), string> history, bool throwIfAhead)
    {
        foreach (var (key, checksum) in history)
        {
            var file = plan.FirstOrDefault(f => f.Namespace == key.Namespace && f.Version == key.Version);
            if (file is null)
            {
                if (throwIfAhead)
                    throw new SchemaVersionException(
                        $"The database records {key.Namespace}/{key.Version:D4}, which this build does not have; run a newer build.");
                continue;
            }

            if (file.Checksum != checksum)
                throw new MigrationChecksumException(
                    $"Applied migration {file.Namespace}/{file.Version:D4}_{file.Name} was edited after it ran (recorded {checksum[..12]}, now {file.Checksum[..12]}). Add a new file instead.");
        }

        // Branches merge in any order, so a not-yet-applied file below the highest applied number still runs, in plan order.
        return plan.Where(f => !history.ContainsKey((f.Namespace, f.Version))).ToList();
    }

    /// <summary>Refuses a run whose options differ from what the first migration recorded in <c>schema_setting</c>.</summary>
    private async Task CheckFixedSettingsAsync(NpgsqlConnection conn, CancellationToken ct)
    {
        await using var exists = new NpgsqlCommand("SELECT to_regclass('schema_setting') IS NOT NULL", conn);
        if (!(bool)(await exists.ExecuteScalarAsync(ct).ConfigureAwait(false))!)
            return;
        await using var cmd = new NpgsqlCommand("SELECT value FROM schema_setting WHERE key = 'gc.partition_count'", conn);
        if (await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false) is string recorded
            && recorded != _options.PartitionCount.ToString(CultureInfo.InvariantCulture))
            throw new MigrationException(
                $"gc.partition_count is {_options.PartitionCount} but this database was created with {recorded}. The count is fixed at the first migrate; changing it needs a planned data move.");
    }

    private static async Task SetSessionOptionAsync(NpgsqlConnection conn, string name, string value, CancellationToken ct)
    {
        await using var cmd = new NpgsqlCommand("SELECT set_config($1, $2, false)", conn)
        {
            Parameters = { new() { Value = name }, new() { Value = value } },
        };
        await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    private static async Task ApplyAsync(NpgsqlConnection conn, MigrationFile file, CancellationToken ct)
    {
        // provider is written equal to namespace; see _historyUpgradeSql.
        const string insert = "INSERT INTO schema_migrations (provider, namespace, version, name, checksum) VALUES ($1, $2, $3, $4, $5)";
        NpgsqlTransaction? tx = file.NoTransaction ? null : await conn.BeginTransactionAsync(ct).ConfigureAwait(false);
        try
        {
            await ExecAsync(conn, tx, file.Sql, ct).ConfigureAwait(false);
            await using var cmd = new NpgsqlCommand(insert, conn, tx)
            {
                Parameters =
                {
                    new() { Value = file.Namespace },
                    new() { Value = file.Namespace },
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
                $"Migration {file.Namespace}/{file.Version:D4}_{file.Name} failed; the database stays at the last good version: {ex.Message}", ex);
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

    private static async Task<bool> HistoryHasNamespaceAsync(NpgsqlConnection conn, CancellationToken ct)
    {
        await using var cmd = new NpgsqlCommand(
            "SELECT EXISTS (SELECT 1 FROM information_schema.columns WHERE table_schema = current_schema() AND table_name = 'schema_migrations' AND column_name = 'namespace')",
            conn);
        return (bool)(await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false))!;
    }

    private static async Task<Dictionary<(string Namespace, int Version), string>> ReadHistoryAsync(NpgsqlConnection conn, CancellationToken ct)
    {
        var history = new Dictionary<(string, int), string>();
        await using var cmd = new NpgsqlCommand("SELECT namespace, version, checksum FROM schema_migrations", conn);
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
