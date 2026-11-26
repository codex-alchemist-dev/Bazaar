using Bazaar.Server.Identity.Kek;
using Bazaar.Testing.Postgres;
using Npgsql;

namespace Bazaar.Server.ColumnSeal.Tests;

/// <summary>
/// The PostgreSQL store against a real database. Skips when no PostgreSQL is available (Windows without BAZAAR_TEST_PG, or no
/// Docker for the container). The migration runner lives in the server project, which a module test does not reference, so the
/// test applies the module's one embedded migration itself, after checking its resource name is the one the runner expects.
/// </summary>
public sealed class PostgresColumnDekStoreTests
{
    private static async Task MigrateAsync(NpgsqlDataSource dataSource, CancellationToken ct)
    {
        // The migration grants to the app role, which the common migration creates in a full install.
        await PgRoles.EnsureNoLoginRoleAsync(dataSource, "bazaar_app", ct);
        var assembly = typeof(PostgresColumnDekStore).Assembly;
        var resource = Assert.Single(assembly.GetManifestResourceNames(), n => n.EndsWith(".Sql.column_seal.0001_column_dek.sql", StringComparison.Ordinal));
        await using var stream = assembly.GetManifestResourceStream(resource)!;
        using var reader = new StreamReader(stream);
        await using var cmd = dataSource.CreateCommand(await reader.ReadToEndAsync(ct));
        await cmd.ExecuteNonQueryAsync(ct);
    }

    private static async Task<PgTestDatabase?> TryCreateAsync(CancellationToken ct)
    {
        if (PgSource.IsUnsupportedHost)
            return null;
        try
        {
            _ = await PgSource.GetSharedAsync();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // No Docker or PostgreSQL could be started on this machine. Only the start is excused; migration errors still fail.
            return null;
        }

        return await PgTestDatabase.CreateAsync("colseal", MigrateAsync, ct);
    }

    [Fact]
    public async Task Keys_round_trip_through_postgres_and_old_values_open_after_retirement()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var db = await TryCreateAsync(ct) ?? throw SkipException();
        var root = Path.Combine(Path.GetTempPath(), "colseal-pg-" + Guid.NewGuid().ToString("N"));
        using var kek = FileKekProvider.Open(Path.Combine(root, "keys", "kek.bin"));
        try
        {
            var plain = SealFixture.Random();
            var ctx = SealFixture.Context();
            SealedColumnValue sealedValue;
            using (var store = new PostgresColumnDekStore(db.DataSource, kek))
                sealedValue = await new ColumnSealer(store).SealAsync(plain, ctx, ct);

            // A new store has an empty cache, so the key is read and unwrapped from the table.
            using var reopened = new PostgresColumnDekStore(db.DataSource, kek);
            var sealer = new ColumnSealer(reopened);
            Assert.Equal(plain, await sealer.OpenAsync(sealedValue, ctx, ct));

            Assert.True(await reopened.RetireAsync(sealedValue.KeyId, ct));
            var fresh = await sealer.SealAsync(plain, ctx, ct);
            Assert.NotEqual(sealedValue.KeyId, fresh.KeyId);
            Assert.Equal(plain, await sealer.OpenAsync(sealedValue, ctx, ct));

            await using var count = db.DataSource.CreateCommand("SELECT count(*) FILTER (WHERE retired_at IS NULL), count(*) FROM column_dek");
            await using var rows = await count.ExecuteReaderAsync(ct);
            Assert.True(await rows.ReadAsync(ct));
            Assert.Equal(1L, rows.GetInt64(0));
            Assert.Equal(2L, rows.GetInt64(1));
        }
        finally
        {
            try
            {
                Directory.Delete(root, recursive: true);
            }
            catch (IOException)
            {
                // A leftover temp folder is harmless.
            }
        }
    }

    [Fact]
    public async Task Two_stores_racing_for_a_new_scope_end_on_one_live_key()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var db = await TryCreateAsync(ct) ?? throw SkipException();
        var root = Path.Combine(Path.GetTempPath(), "colseal-pg-" + Guid.NewGuid().ToString("N"));
        using var kek = FileKekProvider.Open(Path.Combine(root, "keys", "kek.bin"));
        try
        {
            var stores = Enumerable.Range(0, 4).Select(_ => new PostgresColumnDekStore(db.DataSource, kek)).ToList();
            var keys = await Task.WhenAll(stores.Select(s => Task.Run(async () =>
            {
                using var dek = await s.GetOrCreateActiveAsync("gc", ct);
                return dek.DekId;
            }, ct)));
            stores.ForEach(s => s.Dispose());

            Assert.Single(keys.Distinct());
        }
        finally
        {
            try
            {
                Directory.Delete(root, recursive: true);
            }
            catch (IOException)
            {
                // A leftover temp folder is harmless.
            }
        }
    }

    [Fact]
    public async Task The_table_refuses_a_second_live_key_for_a_scope()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var db = await TryCreateAsync(ct) ?? throw SkipException();

        async Task InsertAsync()
        {
            await using var cmd = db.DataSource.CreateCommand("INSERT INTO column_dek (dek_id, scope, wrapped_dek, kek_id) VALUES ($1, 'gc', $2, 'k')");
            cmd.Parameters.AddWithValue(Guid.NewGuid());
            cmd.Parameters.AddWithValue(SealFixture.Random(60));
            await cmd.ExecuteNonQueryAsync(ct);
        }

        await InsertAsync();
        var ex = await Assert.ThrowsAsync<PostgresException>(InsertAsync);
        Assert.Equal("23505", ex.SqlState);
    }

    [Fact]
    public void The_migration_resource_is_in_the_column_seal_namespace()
    {
        var names = typeof(PostgresColumnDekStore).Assembly.GetManifestResourceNames();

        Assert.Contains(names, n => n.EndsWith(".Sql.column_seal.0001_column_dek.sql", StringComparison.Ordinal));
        Assert.DoesNotContain(names, n => n.Contains(".Sql.common.", StringComparison.Ordinal));
    }

    private static InvalidOperationException SkipException()
    {
        Assert.Skip("No PostgreSQL available: Windows without BAZAAR_TEST_PG, or no Docker for the test container.");
        return new InvalidOperationException();
    }
}
