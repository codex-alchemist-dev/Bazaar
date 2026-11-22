using Bazaar.Contracts.Wire;
using Bazaar.Testing.Postgres;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace Bazaar.Server.SchemaIdentity.Tests;

public sealed class SchemaDb : PgFixture, IAsyncLifetime;

public sealed class PostgresSchemaIdentityProviderTests(SchemaDb db) : IClassFixture<SchemaDb>
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static void SkipWithoutDatabase() =>
        Assert.SkipWhen(PgSource.IsUnsupportedHost, "Windows without BAZAAR_TEST_PG: no test PostgreSQL available.");

    private async Task ExecAsync(string sql)
    {
        await using var cmd = db.DataSource.CreateCommand(sql);
        await cmd.ExecuteNonQueryAsync(Ct);
    }

    private async Task ResetHistoryAsync(params (string Namespace, int Version)[] rows)
    {
        await ExecAsync("DROP TABLE IF EXISTS schema_migrations");
        await ExecAsync("CREATE TABLE schema_migrations (namespace text NOT NULL, version int NOT NULL, name text NOT NULL DEFAULT 'x', PRIMARY KEY (namespace, version))");
        foreach (var (ns, version) in rows)
        {
            await using var cmd = db.DataSource.CreateCommand("INSERT INTO schema_migrations (namespace, version) VALUES ($1, $2)");
            cmd.Parameters.AddWithValue(ns);
            cmd.Parameters.AddWithValue(version);
            await cmd.ExecuteNonQueryAsync(Ct);
        }
    }

    [Fact]
    public async Task Identity_matches_the_pure_digest_of_the_applied_rows()
    {
        SkipWithoutDatabase();
        await ResetHistoryAsync(("common", 1), ("common", 2), ("jobs", 1));

        var id = await new PostgresSchemaIdentityProvider(db.DataSource).GetAsync(Ct);

        Assert.Equal(SchemaDigest.Compute([("common", 2), ("jobs", 1)]), id);
    }

    [Fact]
    public async Task Missing_history_table_means_unknown()
    {
        SkipWithoutDatabase();
        await ExecAsync("DROP TABLE IF EXISTS schema_migrations");

        Assert.Null(await new PostgresSchemaIdentityProvider(db.DataSource).GetAsync(Ct));
    }

    [Fact]
    public async Task History_from_before_namespaces_means_unknown()
    {
        SkipWithoutDatabase();
        await ExecAsync("DROP TABLE IF EXISTS schema_migrations");
        await ExecAsync("CREATE TABLE schema_migrations (version int PRIMARY KEY, name text)");

        Assert.Null(await new PostgresSchemaIdentityProvider(db.DataSource).GetAsync(Ct));
    }

    [Fact]
    public async Task Empty_history_means_unknown()
    {
        SkipWithoutDatabase();
        await ResetHistoryAsync();

        Assert.Null(await new PostgresSchemaIdentityProvider(db.DataSource).GetAsync(Ct));
    }

    [Fact]
    public async Task First_successful_read_is_kept()
    {
        SkipWithoutDatabase();
        await ResetHistoryAsync(("common", 1));
        var provider = new PostgresSchemaIdentityProvider(db.DataSource);
        var first = await provider.GetAsync(Ct);

        await ResetHistoryAsync(("common", 5));

        Assert.Equal(first, await provider.GetAsync(Ct));
    }

    [Fact]
    public async Task A_later_read_succeeds_after_an_unknown_one()
    {
        SkipWithoutDatabase();
        await ExecAsync("DROP TABLE IF EXISTS schema_migrations");
        var provider = new PostgresSchemaIdentityProvider(db.DataSource);
        Assert.Null(await provider.GetAsync(Ct));

        await ResetHistoryAsync(("common", 3));

        Assert.Equal(SchemaDigest.Compute([("common", 3)]), await provider.GetAsync(Ct));
    }

    [Fact]
    public void Registration_adds_the_provider()
    {
        var services = new ServiceCollection();
        services.AddSingleton<NpgsqlDataSource>(_ => NpgsqlDataSource.Create("Host=localhost"));
        services.AddBazaarSchemaIdentity();

        using var sp = services.BuildServiceProvider();
        Assert.IsType<PostgresSchemaIdentityProvider>(sp.GetRequiredService<ISchemaIdentityProvider>());
    }
}
