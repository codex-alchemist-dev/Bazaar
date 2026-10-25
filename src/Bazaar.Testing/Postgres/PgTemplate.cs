using Npgsql;

namespace Bazaar.Testing.Postgres;

/// <summary>
/// One migrated template database per process (per test run). Test classes clone it with
/// <c>CREATE DATABASE ... TEMPLATE</c>, which is far cheaper than re-running migrations.
/// </summary>
public sealed class PgTemplate
{
    private static readonly Lock Gate = new();
    private static readonly string RunId = Guid.NewGuid().ToString("N")[..8];
    private static readonly SemaphoreSlim CloneLock = new(1, 1);
    private static Func<string, Task>? migrator;
    private static Task<PgTemplate>? shared;
    private static int cloneCounter;

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
        lock (Gate)
        {
            if (shared is not null)
                throw new InvalidOperationException("The template database is already built; register the migrator before first use.");
            migrator = migrate;
        }
    }

    /// <summary>Builds the process-wide template on first call.</summary>
    public static Task<PgTemplate> GetSharedAsync()
    {
        lock (Gate)
            return shared ??= BuildAsync();
    }

    private static async Task<PgTemplate> BuildAsync()
    {
        var source = await PgSource.GetSharedAsync().ConfigureAwait(false);
        var name = $"bazaar_tpl_{RunId}";
        await ExecAdminAsync(source, $"CREATE DATABASE \"{name}\"").ConfigureAwait(false);
        var template = new PgTemplate(source, name);
        try
        {
            if (migrator is { } migrate)
                await migrate(source.ConnectionStringFor(name, pooling: false)).ConfigureAwait(false);
        }
        catch
        {
            await ExecAdminAsync(source, $"DROP DATABASE IF EXISTS \"{name}\" WITH (FORCE)").ConfigureAwait(false);
            throw;
        }

        NpgsqlConnection.ClearAllPools();
        AppDomain.CurrentDomain.ProcessExit += (_, _) =>
        {
            try { ExecAdminAsync(source, $"DROP DATABASE IF EXISTS \"{name}\" WITH (FORCE)").GetAwaiter().GetResult(); }
            catch (Exception) { /* server may already be gone */ }
        };
        return template;
    }

    /// <summary>Creates a fresh database cloned from the template and returns its name.</summary>
    internal async Task<string> CloneAsync(string label)
    {
        var name = $"bazaar_t_{RunId}_{Interlocked.Increment(ref cloneCounter)}_{Sanitize(label)}";
        if (name.Length > 63)
            name = name[..63];

        // CREATE DATABASE ... TEMPLATE needs the template idle; serialise clones in-process and retry
        // (SQLSTATE 55006 object_in_use) in case another process still holds it.
        await CloneLock.WaitAsync().ConfigureAwait(false);
        try
        {
            for (var attempt = 0; ; attempt++)
            {
                try
                {
                    await ExecAdminAsync(_source, $"CREATE DATABASE \"{name}\" TEMPLATE \"{Name}\"").ConfigureAwait(false);
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
            CloneLock.Release();
        }
    }

    internal Task DropAsync(string database) =>
        ExecAdminAsync(_source, $"DROP DATABASE IF EXISTS \"{database}\" WITH (FORCE)");

    internal string ConnectionStringFor(string database) => _source.ConnectionStringFor(database);

    private static string Sanitize(string label)
    {
        var chars = label.Select(c => char.IsAsciiLetterOrDigit(c) ? char.ToLowerInvariant(c) : '_').ToArray();
        return new string(chars);
    }

    private static async Task ExecAdminAsync(PgSource source, string sql)
    {
        await using var conn = new NpgsqlConnection(source.AdminConnectionString);
        await conn.OpenAsync().ConfigureAwait(false);
        await using var cmd = new NpgsqlCommand(sql, conn);
        await cmd.ExecuteNonQueryAsync().ConfigureAwait(false);
    }
}
