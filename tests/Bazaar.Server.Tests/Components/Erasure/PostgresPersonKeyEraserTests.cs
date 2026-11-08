using Bazaar.Server.Components.Erasure;
using Bazaar.Server.Data.Migrations;
using Bazaar.Testing.Postgres;
using Npgsql;

namespace Bazaar.Server.Tests.Components.Erasure;

/// <summary>A cloned database with the common migrations applied, for the person_key erasure path.</summary>
public sealed class ErasureDb : PgFixture, IAsyncLifetime
{
    private NpgsqlDataSource? _pool;

    public new NpgsqlDataSource DataSource => _pool ?? throw new InvalidOperationException("ErasureDb is not initialised.");

    public override async ValueTask InitializeAsync()
    {
        if (PgSource.IsUnsupportedHost)
            return;
        await base.InitializeAsync();
        _pool = NpgsqlDataSource.Create(new NpgsqlConnectionStringBuilder(ConnectionString) { MaxPoolSize = 4 }.ConnectionString);
        await new MigrationRunner(DataSource, new MigrationOptions(16))
            .MigrateAsync(new EmbeddedMigrationSource(typeof(MigrationRunner).Assembly), "linux", CancellationToken.None);
    }
}

/// <summary>Key destruction through the real <c>erase_person_key</c> function (migration 0008) on a real PostgreSQL.</summary>
public sealed class PostgresPersonKeyEraserTests(ErasureDb db) : IClassFixture<ErasureDb>
{
    private static readonly Guid _person = Guid.Parse("0197a000-0000-7000-8000-0000000000e2");

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Destroys_the_live_key_once_and_then_reports_it_already_destroyed()
    {
        Assert.SkipWhen(PgSource.IsUnsupportedHost, "Windows without BAZAAR_TEST_PG: no test PostgreSQL available.");
        await using (var insert = db.DataSource.CreateCommand(
            "INSERT INTO person_key (person_id, wrapped_key) VALUES ($1, '\\x0102'::bytea)"))
        {
            insert.Parameters.Add(new NpgsqlParameter<Guid> { TypedValue = _person });
            await insert.ExecuteNonQueryAsync(Ct);
        }

        var eraser = new PostgresPersonKeyEraser(db.DataSource);

        var first = await eraser.EraseAsync(_person, Ct);
        var second = await eraser.EraseAsync(_person, Ct);

        Assert.Equal(new PersonKeyEraseResult(Destroyed: 1, AlreadyDestroyed: 0), first);
        Assert.Equal(new PersonKeyEraseResult(Destroyed: 0, AlreadyDestroyed: 1), second);

        await using var check = db.DataSource.CreateCommand(
            "SELECT wrapped_key IS NULL AND destroyed_at IS NOT NULL FROM person_key WHERE person_id = $1");
        check.Parameters.Add(new NpgsqlParameter<Guid> { TypedValue = _person });
        Assert.True((bool)(await check.ExecuteScalarAsync(Ct))!);
    }
}
