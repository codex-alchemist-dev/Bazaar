using Bazaar.Server.Data.Migrations;
using Bazaar.Server.Security.Audit.Db;
using Bazaar.Testing.Postgres;
using Npgsql;

namespace Bazaar.Server.Tests.Security.Audit.Db;

/// <summary>
/// A fresh database per test, created from template0 and migrated with the real runner (M1), so migration 0004 runs
/// exactly as in production. Tests that look at the whole chain need this: rows from other tests would change the chain.
/// </summary>
internal sealed class AuditTestDb : IAsyncDisposable
{
    /// <summary>Superuser connection: the test-only owner of the database.</summary>
    public NpgsqlDataSource DataSource { get; }

    private readonly string _name;
    private readonly string _adminConnectionString;

    private AuditTestDb(string name, string adminConnectionString, string connectionString, NpgsqlDataSource dataSource)
    {
        _name = name;
        ConnectionString = connectionString;
        _adminConnectionString = adminConnectionString;
        DataSource = dataSource;
    }

    /// <summary>
    /// Connection string for this database as the superuser; used to build a data source for another role. Kept from
    /// the source rather than read back from <see cref="DataSource"/>, because Npgsql strips the password there.
    /// </summary>
    public string ConnectionString { get; }

    public static async Task<AuditTestDb> CreateAsync(CancellationToken ct)
    {
        var source = await PgSource.GetSharedAsync().ConfigureAwait(false);
        var name = $"audit_{Guid.NewGuid():N}";
        await AdminAsync(source.AdminConnectionString, $"CREATE DATABASE \"{name}\" TEMPLATE template0", ct).ConfigureAwait(false);

        var connectionString = source.ConnectionStringFor(name);
        var dataSource = NpgsqlDataSource.Create(connectionString);
        var db = new AuditTestDb(name, source.AdminConnectionString, connectionString, dataSource);
        try
        {
            // Roles are cluster-wide and other test databases migrate at the same time: tolerate a role that already exists.
            await PgRoles.EnsureNoLoginRoleAsync(dataSource, "bazaar_app", ct).ConfigureAwait(false);
            await new MigrationRunner(dataSource).MigrateAsync(
                new EmbeddedMigrationSource(typeof(PostgresAuditLog).Assembly), MigrateCommand.CurrentProvider(), ct).ConfigureAwait(false);
        }
        catch
        {
            await db.DisposeAsync().ConfigureAwait(false);
            throw;
        }

        return db;
    }

    /// <summary>
    /// A data source whose physical connections run as <paramref name="role"/>. The superuser test connection can
    /// SET ROLE to any role; the caller disposes the returned source.
    /// </summary>
    public NpgsqlDataSource AsRole(string role)
    {
        var builder = new NpgsqlDataSourceBuilder(ConnectionString);
        builder.UsePhysicalConnectionInitializer(
            conn => ExecSync(conn, $"SET ROLE {role}"),
            async conn => await ExecAsync(conn, $"SET ROLE {role}", CancellationToken.None).ConfigureAwait(false));
        return builder.Build();
    }

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

    private static void ExecSync(NpgsqlConnection conn, string sql)
    {
        using var cmd = new NpgsqlCommand(sql, conn);
        cmd.ExecuteNonQuery();
    }

    private static async Task ExecAsync(NpgsqlConnection conn, string sql, CancellationToken ct)
    {
        await using var cmd = new NpgsqlCommand(sql, conn);
        await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }
}
