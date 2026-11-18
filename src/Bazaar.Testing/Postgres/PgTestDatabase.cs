using Npgsql;

namespace Bazaar.Testing.Postgres;

/// <summary>
/// A fresh database per test, created from template0 and migrated by the caller, so one test's rows never leak into another.
/// The caller supplies the migration step, because the migration runner lives in the server project this package does not reference.
/// </summary>
public sealed class PgTestDatabase : IAsyncDisposable
{
    private readonly string _name;
    private readonly string _adminConnectionString;

    /// <summary>The database's pool. Tests use it for their own writes and for the store under test.</summary>
    public NpgsqlDataSource DataSource { get; }

    /// <summary>The connection string the database was opened with, password included, for pools a test builds itself.</summary>
    public string ConnectionString { get; }

    private PgTestDatabase(string name, string adminConnectionString, string connectionString, NpgsqlDataSource dataSource)
    {
        _name = name;
        _adminConnectionString = adminConnectionString;
        ConnectionString = connectionString;
        DataSource = dataSource;
    }

    /// <summary>Creates the database and runs <paramref name="migrate"/> on it. Disposes it again if the migration throws.</summary>
    public static async Task<PgTestDatabase> CreateAsync(
        string prefix,
        Func<NpgsqlDataSource, CancellationToken, Task> migrate,
        CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(prefix);
        ArgumentNullException.ThrowIfNull(migrate);

        var source = await PgSource.GetSharedAsync().ConfigureAwait(false);
        var name = $"{prefix}_{Guid.NewGuid():N}";
        await AdminAsync(source.AdminConnectionString, $"CREATE DATABASE \"{name}\" TEMPLATE template0", ct).ConfigureAwait(false);

        var connectionString = source.ConnectionStringFor(name);
        var dataSource = NpgsqlDataSource.Create(connectionString);
        var db = new PgTestDatabase(name, source.AdminConnectionString, connectionString, dataSource);
        try
        {
            await migrate(dataSource, ct).ConfigureAwait(false);
        }
        catch
        {
            await db.DisposeAsync().ConfigureAwait(false);
            throw;
        }

        return db;
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        await DataSource.DisposeAsync().ConfigureAwait(false);
        await AdminAsync(_adminConnectionString, $"DROP DATABASE IF EXISTS \"{_name}\" WITH (FORCE)", CancellationToken.None).ConfigureAwait(false);
    }

    private static async Task AdminAsync(string adminConnectionString, string sql, CancellationToken ct)
    {
        await using var conn = new NpgsqlConnection(adminConnectionString);
        await conn.OpenAsync(ct).ConfigureAwait(false);
        await using var cmd = new NpgsqlCommand(sql, conn);
        await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }
}
