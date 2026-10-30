using Bazaar.Server.Data.Migrations;
using Bazaar.Server.Tests.Data.Migrations;
using Bazaar.Testing.Postgres;
using Npgsql;

namespace Bazaar.Server.Tests.Data;

/// <summary>Migration <c>common/0001_core_schema.sql</c> on real PostgreSQL (doc 01, doc 04, spike S3).</summary>
public sealed class CoreSchemaTests(MigrationDb db) : IClassFixture<MigrationDb>
{
    private static readonly EmbeddedMigrationSource _source = new(typeof(MigrationRunner).Assembly);

    private static readonly string[] _partitioned = ["gc_event", "gc_message", "gc_message_version", "gc_mention"];

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private async Task<NpgsqlDataSource> MigratedAsync(int partitions = 16)
    {
        Assert.SkipWhen(PgSource.IsUnsupportedHost, "Windows without BAZAAR_TEST_PG: no test PostgreSQL available.");
        await using (var reset = db.DataSource.CreateCommand("DROP SCHEMA public CASCADE; CREATE SCHEMA public"))
            await reset.ExecuteNonQueryAsync(Ct);
        await new MigrationRunner(db.DataSource, new MigrationOptions(partitions)).MigrateAsync(_source, "linux", Ct);
        return db.DataSource;
    }

    private static async Task<long> CountAsync(NpgsqlDataSource ds, string sql)
    {
        await using var cmd = ds.CreateCommand(sql);
        return Convert.ToInt64(await cmd.ExecuteScalarAsync(Ct), System.Globalization.CultureInfo.InvariantCulture);
    }

    private static async Task ExecAsync(NpgsqlConnection conn, string sql)
    {
        await using var cmd = new NpgsqlCommand(sql, conn);
        await cmd.ExecuteNonQueryAsync(Ct);
    }

    private static readonly Guid _gcId = Guid.Parse("0197a000-0000-7000-8000-000000000001");
    private static readonly Guid _author = Guid.Parse("0197a000-0000-7000-8000-0000000000aa");

    private static async Task SeedGcAsync(NpgsqlConnection conn)
    {
        await ExecAsync(conn, $"INSERT INTO gc (gc_id, type_key, title, created_by) VALUES ('{_gcId}', 'general', 'General', '{_author}')");
        await ExecAsync(conn,
            $"INSERT INTO gc_event (gc_id, seq, kind, author_kind, author_id, source_trust, client_msg_id) VALUES ('{_gcId}', 1, 'message', 'agent', '{_author}', 'agent', 'c1')");
    }

    [Fact]
    public async Task Embedded_source_applies_and_creates_sixteen_partitions_per_log_table_by_default()
    {
        var ds = await MigratedAsync();

        foreach (var table in _partitioned)
            Assert.Equal(16, await CountAsync(ds, $"SELECT count(*) FROM pg_inherits WHERE inhparent = '{table}'::regclass"));
        Assert.Equal("16", (await CountAsync(ds, "SELECT value::bigint FROM schema_setting WHERE key = 'gc.partition_count'")).ToString(System.Globalization.CultureInfo.InvariantCulture));
    }

    [Fact]
    public async Task Partition_count_follows_the_option()
    {
        var ds = await MigratedAsync(4);

        foreach (var table in _partitioned)
            Assert.Equal(4, await CountAsync(ds, $"SELECT count(*) FROM pg_inherits WHERE inhparent = '{table}'::regclass"));
    }

    [Fact]
    public async Task A_different_partition_count_after_the_first_migrate_is_refused()
    {
        var ds = await MigratedAsync(8);

        await new MigrationRunner(ds, new MigrationOptions(8)).MigrateAsync(_source, "linux", Ct);
        var ex = await Assert.ThrowsAsync<MigrationException>(
            () => new MigrationRunner(ds, new MigrationOptions(16)).MigrateAsync(_source, "linux", Ct));
        Assert.Contains("partition_count", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Seed_rows_are_present()
    {
        var ds = await MigratedAsync();

        Assert.Equal(14, await CountAsync(ds, "SELECT count(*) FROM gc_type"));
        Assert.Equal(1, await CountAsync(ds, "SELECT count(*) FROM gc_type WHERE type_key = 'dm' AND default_visibility = 'two_members_owner'"));
        Assert.Equal(11, await CountAsync(ds, "SELECT count(*) FROM gc_event_kind"));
        Assert.Equal(2, await CountAsync(ds, "SELECT count(*) FROM gc_event_kind WHERE kind IN ('message', 'message_edit')"));
    }

    [Fact]
    public async Task Log_tables_are_append_only_even_for_the_owner()
    {
        var ds = await MigratedAsync();
        await using var conn = await ds.OpenConnectionAsync(Ct);
        await SeedGcAsync(conn);
        await ExecAsync(conn, $"INSERT INTO gc_message (gc_id, message_id, first_seq, author_id) VALUES ('{_gcId}', '{_author}', 1, '{_author}')");
        await ExecAsync(conn,
            $"INSERT INTO gc_message_version (gc_id, message_id, version, seq, body_ct, editor_id) VALUES ('{_gcId}', '{_author}', 1, 1, '\\x00', '{_author}')");

        foreach (var sql in new[]
        {
            $"UPDATE gc_event SET kind = 'system' WHERE gc_id = '{_gcId}'",
            $"DELETE FROM gc_event WHERE gc_id = '{_gcId}'",
            $"UPDATE gc_message SET author_id = '{_gcId}' WHERE gc_id = '{_gcId}'",
            $"DELETE FROM gc_message_version WHERE gc_id = '{_gcId}'",
            "TRUNCATE gc_event",
        })
        {
            var ex = await Assert.ThrowsAsync<PostgresException>(() => ExecAsync(conn, sql));
            // TRUNCATE is not covered by the row trigger. It is refused by the foreign key from wake_decisions (0016),
            // which PostgreSQL reports as 0A000. An explicit BEFORE TRUNCATE trigger would make it 23001 too.
            var expected = sql.StartsWith("TRUNCATE", StringComparison.Ordinal) ? new[] { "23001", "0A000" } : new[] { "23001" };
            Assert.Contains(ex.SqlState, expected);
        }

        Assert.Equal(1, await CountAsync(ds, "SELECT count(*) FROM gc_event"));
    }

    [Fact]
    public async Task App_role_can_append_and_bump_head_seq_but_cannot_rewrite_or_run_ddl()
    {
        var ds = await MigratedAsync();
        await using var conn = await ds.OpenConnectionAsync(Ct);
        await ExecAsync(conn, "SET ROLE bazaar_app");
        try
        {
            await SeedGcAsync(conn);
            await ExecAsync(conn, $"UPDATE gc SET head_seq = head_seq + 1 WHERE gc_id = '{_gcId}'");
            await ExecAsync(conn, $"INSERT INTO gc_mention (gc_id, seq, target_principal_id) VALUES ('{_gcId}', 1, '{_author}')");

            foreach (var sql in new[]
            {
                $"UPDATE gc_event SET kind = 'system' WHERE gc_id = '{_gcId}'",
                $"DELETE FROM gc_event WHERE gc_id = '{_gcId}'",
                $"UPDATE gc SET created_by = '{_gcId}' WHERE gc_id = '{_gcId}'",
                "UPDATE schema_setting SET value = '1'",
                "CREATE TABLE app_made_this (id int)",
            })
            {
                var ex = await Assert.ThrowsAsync<PostgresException>(() => ExecAsync(conn, sql));
                Assert.Equal("42501", ex.SqlState);
            }
        }
        finally
        {
            await ExecAsync(conn, "RESET ROLE");
        }

        Assert.Equal(1, await CountAsync(ds, "SELECT head_seq FROM gc"));
    }

    [Fact]
    public async Task A_lookup_by_gc_id_prunes_to_one_partition()
    {
        var ds = await MigratedAsync();
        await using var conn = await ds.OpenConnectionAsync(Ct);
        await SeedGcAsync(conn);

        await using var cmd = new NpgsqlCommand($"EXPLAIN SELECT * FROM gc_event WHERE gc_id = '{_gcId}' AND seq > 0", conn);
        var plan = new List<string>();
        await using (var reader = await cmd.ExecuteReaderAsync(Ct))
            while (await reader.ReadAsync(Ct))
                plan.Add(reader.GetString(0));

        var partitionsScanned = plan.SelectMany(l => System.Text.RegularExpressions.Regex.Matches(l, @"gc_event_p\d+").Select(m => m.Value)).Distinct().ToList();
        Assert.Single(partitionsScanned);
    }

    [Fact]
    public async Task Idempotency_key_and_row_shape_constraints_hold()
    {
        var ds = await MigratedAsync();
        await using var conn = await ds.OpenConnectionAsync(Ct);
        await SeedGcAsync(conn);

        var duplicate = await Assert.ThrowsAsync<PostgresException>(() => ExecAsync(conn,
            $"INSERT INTO gc_event (gc_id, seq, kind, author_kind, author_id, source_trust, client_msg_id) VALUES ('{_gcId}', 2, 'message', 'agent', '{_author}', 'agent', 'c1')"));
        Assert.Equal("23505", duplicate.SqlState);

        var badTrust = await Assert.ThrowsAsync<PostgresException>(() => ExecAsync(conn,
            $"INSERT INTO gc_event (gc_id, seq, kind, author_kind, author_id, source_trust) VALUES ('{_gcId}', 2, 'message', 'agent', '{_author}', 'trusted-ish')"));
        Assert.Equal("23514", badTrust.SqlState);

        var unknownKind = await Assert.ThrowsAsync<PostgresException>(() => ExecAsync(conn,
            $"INSERT INTO gc_event (gc_id, seq, kind, author_kind, author_id, source_trust) VALUES ('{_gcId}', 2, 'made-up', 'agent', '{_author}', 'agent')"));
        Assert.Equal("23503", unknownKind.SqlState);

        await ExecAsync(conn, $"INSERT INTO gc_message (gc_id, message_id, first_seq, author_id) VALUES ('{_gcId}', '{_author}', 1, '{_author}')");
        var tombstoneWithBody = await Assert.ThrowsAsync<PostgresException>(() => ExecAsync(conn,
            $"INSERT INTO gc_message_version (gc_id, message_id, version, seq, body_ct, tombstone, editor_id) VALUES ('{_gcId}', '{_author}', 1, 1, '\\x00', true, '{_author}')"));
        Assert.Equal("23514", tombstoneWithBody.SqlState);
    }
}
