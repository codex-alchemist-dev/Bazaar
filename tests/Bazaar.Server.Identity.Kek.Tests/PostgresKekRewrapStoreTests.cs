using System.Security.Cryptography;
using Bazaar.Server.Identity.Kek;
using Npgsql;

namespace Bazaar.Server.Identity.Kek.Tests;

/// <summary>
/// The Postgres re-wrap store against a real database. Runs when <c>BAZAAR_TEST_PG</c> holds a connection string (CI Postgres jobs).
/// It creates a throwaway schema with the <c>person_key</c> columns the store uses, and drops it afterwards.
/// </summary>
public sealed class PostgresKekRewrapStoreTests
{
    private static string? ConnectionString => Environment.GetEnvironmentVariable("BAZAAR_TEST_PG");

    [Fact]
    public async Task Rewrap_moves_person_keys_in_a_real_database_and_a_rerun_is_empty()
    {
        Assert.SkipUnless(!string.IsNullOrWhiteSpace(ConnectionString), "Set BAZAAR_TEST_PG to a PostgreSQL connection string to run this test.");
        var ct = TestContext.Current.CancellationToken;
        var schema = "kek_rewrap_" + Guid.CreateVersion7().ToString("N");
        var builder = new NpgsqlConnectionStringBuilder(ConnectionString!) { SearchPath = schema };
        await using var dataSource = NpgsqlDataSource.Create(builder.ConnectionString);
        await using (var admin = new NpgsqlConnection(ConnectionString))
        {
            await admin.OpenAsync(ct);
            await using var create = new NpgsqlCommand($"CREATE SCHEMA \"{schema}\"; CREATE TABLE \"{schema}\".person_key (key_id uuid PRIMARY KEY, person_id uuid NOT NULL, wrapped_key bytea, created_at timestamptz NOT NULL DEFAULT now(), destroyed_at timestamptz, CHECK ((destroyed_at IS NULL) = (wrapped_key IS NOT NULL)));", admin);
            await create.ExecuteNonQueryAsync(ct);
        }

        try
        {
            using var source = FileKekProvider.Open(Path.Combine(Path.GetTempPath(), schema, "keys", "kek.bin"));
            using var target = DpapiKekProvider.Open(Path.Combine(Path.GetTempPath(), schema, "keys", "kek-dpapi.bin"), new TestProtector(RandomNumberGenerator.GetBytes(32)));
            var keyIds = new List<Guid>();
            await using (var insert = dataSource.CreateCommand("INSERT INTO person_key (key_id, person_id, wrapped_key) VALUES ($1, $2, $3)"))
            {
                for (var i = 0; i < 5; i++)
                {
                    var keyId = Guid.CreateVersion7();
                    keyIds.Add(keyId);
                    insert.Parameters.Clear();
                    insert.Parameters.Add(new NpgsqlParameter<Guid> { TypedValue = keyId });
                    insert.Parameters.Add(new NpgsqlParameter<Guid> { TypedValue = Guid.NewGuid() });
                    insert.Parameters.Add(new NpgsqlParameter<byte[]> { TypedValue = await source.WrapAsync(RandomNumberGenerator.GetBytes(32), keyId.ToByteArray(), ct) });
                    await insert.ExecuteNonQueryAsync(ct);
                }
            }

            var store = new PostgresKekRewrapStore(dataSource);
            var audit = new NullAudit();
            var report = await KekRewriter.RunAsync(source, target, store, audit, batchSize: 2, ct: ct);

            Assert.True(report.Succeeded);
            Assert.Equal(5, report.Rewrapped);
            var rows = await store.ListLiveWrapsAsync(null, 100, ct);
            Assert.Equal(5, rows.Count);
            foreach (var row in rows)
                await target.UnwrapAsync(row.Wrapped, row.AssociatedData, ct);

            var rerun = await KekRewriter.RunAsync(source, target, store, audit, batchSize: 2, ct: ct);
            Assert.Equal(0, rerun.Rewrapped);
            Assert.Equal(5, rerun.AlreadyCurrent);
        }
        finally
        {
            await using var admin = new NpgsqlConnection(ConnectionString);
            await admin.OpenAsync(ct);
            await using var drop = new NpgsqlCommand($"DROP SCHEMA IF EXISTS \"{schema}\" CASCADE;", admin);
            await drop.ExecuteNonQueryAsync(ct);
            Directory.Delete(Path.Combine(Path.GetTempPath(), schema), recursive: true);
        }
    }

    [Fact]
    public async Task Soul_deks_create_reuse_shred_and_rewrap_in_a_real_database()
    {
        Assert.SkipUnless(!string.IsNullOrWhiteSpace(ConnectionString), "Set BAZAAR_TEST_PG to a PostgreSQL connection string to run this test.");
        var ct = TestContext.Current.CancellationToken;
        var schema = "kek_dek_" + Guid.CreateVersion7().ToString("N");
        var builder = new NpgsqlConnectionStringBuilder(ConnectionString!) { SearchPath = schema };
        await using var dataSource = NpgsqlDataSource.Create(builder.ConnectionString);
        await using (var admin = new NpgsqlConnection(ConnectionString))
        {
            await admin.OpenAsync(ct);
            // The soul_dek columns, CHECK and one-live-per-scope index from common/0019_identity_keys.sql.
            await using var create = new NpgsqlCommand(
                $"CREATE SCHEMA \"{schema}\"; SET search_path = \"{schema}\"; "
                + "CREATE TABLE soul_dek (dek_id uuid PRIMARY KEY, scope text NOT NULL CHECK (scope <> ''), wrapped_dek bytea NULL, kek_id text NOT NULL CHECK (kek_id <> ''), created_at timestamptz NOT NULL DEFAULT now(), retired_at timestamptz NULL, CONSTRAINT soul_dek_wrapped_while_live CHECK (retired_at IS NOT NULL OR wrapped_dek IS NOT NULL)); "
                + "CREATE UNIQUE INDEX soul_dek_one_live_idx ON soul_dek (scope) WHERE retired_at IS NULL;",
                admin);
            await create.ExecuteNonQueryAsync(ct);
        }

        try
        {
            using var source = FileKekProvider.Open(Path.Combine(Path.GetTempPath(), schema, "keys", "kek.bin"));
            using var target = DpapiKekProvider.Open(Path.Combine(Path.GetTempPath(), schema, "keys", "kek-dpapi.bin"), new TestProtector(RandomNumberGenerator.GetBytes(32)));
            var store = new PostgresSoulDekStore(dataSource);
            var service = new SoulDekService(source, store);

            using var soul = await service.GetOrCreateAsync("soul-1", ct);
            using var again = await service.GetOrCreateAsync("soul-1", ct);
            using var install = await service.GetOrCreateAsync("install", ct);
            Assert.Equal(soul.DekId, again.DekId);
            Assert.False(await store.TryInsertLiveAsync(Guid.CreateVersion7(), "soul-1", [1], "x", ct));

            var report = await KekRewriter.RunAsync(source, target, [new PostgresSoulDekRewrapStore(dataSource)], new NullAudit(), batchSize: 1, ct: ct);
            Assert.True(report.Succeeded);
            Assert.Equal(2, report.Rewrapped);
            var row = await store.GetLiveAsync("soul-1", ct);
            Assert.Equal(target.KekId, row!.KekId);
            using var reopened = await new SoulDekService(target, store).OpenAsync("soul-1", ct);
            Assert.True(reopened!.Key.Span.SequenceEqual(soul.Key.Span));

            Assert.True(await new SoulDekService(target, store).ShredAsync("soul-1", ct));
            Assert.Null(await store.GetLiveAsync("soul-1", ct));
            var rerun = await KekRewriter.RunAsync(source, target, [new PostgresSoulDekRewrapStore(dataSource)], new NullAudit(), ct: ct);
            Assert.Equal(1, rerun.Scanned);
        }
        finally
        {
            await using var admin = new NpgsqlConnection(ConnectionString);
            await admin.OpenAsync(ct);
            await using var drop = new NpgsqlCommand($"DROP SCHEMA IF EXISTS \"{schema}\" CASCADE;", admin);
            await drop.ExecuteNonQueryAsync(ct);
            Directory.Delete(Path.Combine(Path.GetTempPath(), schema), recursive: true);
        }
    }

    private sealed class NullAudit : IKekAuditSink
    {
        public Task RecordAsync(string kind, string payloadJson, CancellationToken ct) => Task.CompletedTask;
    }
}
