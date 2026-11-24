using Bazaar.Server.Constellation.Memory;
using Bazaar.Server.Data.Migrations;
using Bazaar.Testing.Postgres;
using Npgsql;

namespace Bazaar.Server.Constellation.Tests.Memory.ReadLog;

/// <summary>A cloned test database migrated with the server's files and the Constellation module's component files (constellation/0003 included).</summary>
public sealed class ReadLogDb : PgFixture, IAsyncLifetime
{
    private NpgsqlDataSource? _pool;

    public new NpgsqlDataSource DataSource => _pool ?? throw new InvalidOperationException("ReadLogDb is not initialised.");

    public override async ValueTask InitializeAsync()
    {
        if (PgSource.IsUnsupportedHost)
            return;
        await base.InitializeAsync();
        _pool = NpgsqlDataSource.Create(new NpgsqlConnectionStringBuilder(ConnectionString) { MaxPoolSize = 12 }.ConnectionString);
        var server = new EmbeddedMigrationSource(typeof(Bazaar.Server.AssemblyMarker).Assembly);
        var source = new InMemoryMigrationSource(server.Namespaces().SelectMany(server.Load).Concat(ConstellationMigrations.Files()));
        await new MigrationRunner(DataSource, new MigrationOptions(16)).MigrateAsync(source, "linux", CancellationToken.None);
    }

    /// <summary>A data source whose connections run as <paramref name="role"/> (the superuser test connection can SET ROLE).</summary>
    public NpgsqlDataSource AsRole(string role)
    {
        var builder = new NpgsqlDataSourceBuilder(ConnectionString);
        builder.UsePhysicalConnectionInitializer(
            conn =>
            {
                using var cmd = new NpgsqlCommand($"SET ROLE {role}", conn);
                cmd.ExecuteNonQuery();
            },
            async conn =>
            {
                await using var cmd = new NpgsqlCommand($"SET ROLE {role}", conn);
                await cmd.ExecuteNonQueryAsync();
            });
        return builder.Build();
    }
}
