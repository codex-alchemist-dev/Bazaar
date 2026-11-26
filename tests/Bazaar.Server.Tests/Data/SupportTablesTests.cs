using Bazaar.Server.Data.Migrations;
using Bazaar.Server.Tests.Data.Migrations;
using Bazaar.Testing.Postgres;
using Npgsql;

namespace Bazaar.Server.Tests.Data;

/// <summary>Migration <c>common/0008_gc_support_tables.sql</c> on real PostgreSQL (doc 01, doc 04).</summary>
public sealed class SupportTablesTests(MigrationDb db) : IClassFixture<MigrationDb>
{
    private static readonly EmbeddedMigrationSource _source = new(typeof(MigrationRunner).Assembly);

    private static readonly Guid _gcId = Guid.Parse("0197a000-0000-7000-8000-000000000001");
    private static readonly Guid _who = Guid.Parse("0197a000-0000-7000-8000-0000000000aa");
    private static readonly Guid _other = Guid.Parse("0197a000-0000-7000-8000-0000000000bb");

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private async Task<NpgsqlDataSource> MigratedAsync(int partitions = 16)
    {
        Assert.SkipWhen(PgSource.IsUnsupportedHost, "Windows without BAZAAR_TEST_PG: no test PostgreSQL available.");
        await using (var reset = db.DataSource.CreateCommand("DROP SCHEMA public CASCADE; CREATE SCHEMA public"))
            await reset.ExecuteNonQueryAsync(Ct);
        await new MigrationRunner(db.DataSource, new MigrationOptions(partitions)).MigrateAsync(_source, "linux", Ct);
        await using var gc = db.DataSource.CreateCommand(
            $"INSERT INTO gc (gc_id, type_key, title, created_by) VALUES ('{_gcId}', 'general', 'General', '{_who}')");
        await gc.ExecuteNonQueryAsync(Ct);
        return db.DataSource;
    }

    private static async Task ExecAsync(NpgsqlConnection conn, string sql)
    {
        await using var cmd = new NpgsqlCommand(sql, conn);
        await cmd.ExecuteNonQueryAsync(Ct);
    }

    private static async Task<object?> ScalarAsync(NpgsqlConnection conn, string sql)
    {
        await using var cmd = new NpgsqlCommand(sql, conn);
        return await cmd.ExecuteScalarAsync(Ct);
    }

    private static async Task<long> CountAsync(NpgsqlConnection conn, string sql) =>
        Convert.ToInt64(await ScalarAsync(conn, sql), System.Globalization.CultureInfo.InvariantCulture);

    private static async Task<string> SqlStateAsync(NpgsqlConnection conn, string sql) =>
        (await Assert.ThrowsAsync<PostgresException>(() => ExecAsync(conn, sql))).SqlState;

    [Theory]
    [InlineData(4)]
    [InlineData(16)]
    public async Task Entry_ref_partition_count_follows_the_option(int partitions)
    {
        var ds = await MigratedAsync(partitions);
        await using var conn = await ds.OpenConnectionAsync(Ct);

        Assert.Equal(partitions, await CountAsync(conn, "SELECT count(*) FROM pg_inherits WHERE inhparent = 'entry_ref'::regclass"));
    }

    [Fact]
    public async Task Entry_ref_is_append_only_and_checks_the_kind()
    {
        var ds = await MigratedAsync();
        await using var conn = await ds.OpenConnectionAsync(Ct);
        await ExecAsync(conn, $"INSERT INTO entry_ref (gc_id, seq, ref_kind, ref_id) VALUES ('{_gcId}', 1, 'pr', '42')");

        Assert.Equal("23001", await SqlStateAsync(conn, "UPDATE entry_ref SET ref_id = '43'"));
        Assert.Equal("23001", await SqlStateAsync(conn, "DELETE FROM entry_ref"));
        Assert.Equal("23514", await SqlStateAsync(conn, $"INSERT INTO entry_ref (gc_id, seq, ref_kind, ref_id) VALUES ('{_gcId}', 1, 'nope', '1')"));
    }

    [Fact]
    public async Task App_role_moves_cursors_forward_but_touches_nothing_else()
    {
        var ds = await MigratedAsync();
        await using var conn = await ds.OpenConnectionAsync(Ct);
        await ExecAsync(conn, "SET ROLE bazaar_app");
        try
        {
            await ExecAsync(conn, $"INSERT INTO read_state (principal_id, gc_id) VALUES ('{_who}', '{_gcId}')");
            await ExecAsync(conn, $"UPDATE read_state SET last_read_seq = 5 WHERE principal_id = '{_who}'");
            await ExecAsync(conn, $"INSERT INTO consumer_cursor (consumer, gc_id) VALUES ('notifier', '{_gcId}')");
            await ExecAsync(conn, "UPDATE consumer_cursor SET last_seq = 9");

            Assert.Equal("42501", await SqlStateAsync(conn, $"UPDATE read_state SET gc_id = '{_gcId}'"));
            Assert.Equal("42501", await SqlStateAsync(conn, "DELETE FROM read_state"));
        }
        finally
        {
            await ExecAsync(conn, "RESET ROLE");
        }

        Assert.Equal(5, await CountAsync(conn, "SELECT last_read_seq FROM read_state"));
        Assert.Equal(9, await CountAsync(conn, "SELECT last_seq FROM consumer_cursor"));
    }

    [Fact]
    public async Task Access_request_decisions_need_a_decider_and_cannot_change_the_requester()
    {
        var ds = await MigratedAsync();
        await using var conn = await ds.OpenConnectionAsync(Ct);
        await ExecAsync(conn, "SET ROLE bazaar_app");
        try
        {
            await ExecAsync(conn, $"INSERT INTO gc_access_request (gc_id, requester) VALUES ('{_gcId}', '{_other}')");

            Assert.Equal("23514", await SqlStateAsync(conn, "UPDATE gc_access_request SET status = 'approved'"));
            await ExecAsync(conn, $"UPDATE gc_access_request SET status = 'approved', decided_by = '{_who}', decided_at = now()");
            Assert.Equal("42501", await SqlStateAsync(conn, $"UPDATE gc_access_request SET requester = '{_who}'"));
            Assert.Equal("23514", await SqlStateAsync(conn, "UPDATE gc_access_request SET status = 'maybe'"));
        }
        finally
        {
            await ExecAsync(conn, "RESET ROLE");
        }
    }

    [Fact]
    public async Task Person_key_is_destroyed_only_through_the_erasure_function_and_only_once()
    {
        var ds = await MigratedAsync();
        await using var conn = await ds.OpenConnectionAsync(Ct);
        var keyId = (Guid)(await ScalarAsync(conn, $"INSERT INTO person_key (person_id, wrapped_key) VALUES ('{_who}', '\\x0102') RETURNING key_id"))!;

        Assert.Equal("23001", await SqlStateAsync(conn, $"DELETE FROM person_key WHERE key_id = '{keyId}'"));
        Assert.Equal("23001", await SqlStateAsync(conn, $"UPDATE person_key SET person_id = '{_other}'"));

        await ExecAsync(conn, "SET ROLE bazaar_app");
        try
        {
            Assert.Equal("42501", await SqlStateAsync(conn, $"SELECT erase_person_key('{keyId}')"));
            Assert.Equal("42501", await SqlStateAsync(conn, "UPDATE person_key SET wrapped_key = NULL, destroyed_at = now()"));
        }
        finally
        {
            await ExecAsync(conn, "RESET ROLE");
        }

        await ExecAsync(conn, "SET ROLE bazaar_erasure");
        try
        {
            Assert.True((bool)(await ScalarAsync(conn, $"SELECT erase_person_key('{keyId}')"))!);
            Assert.False((bool)(await ScalarAsync(conn, $"SELECT erase_person_key('{keyId}')"))!);
        }
        finally
        {
            await ExecAsync(conn, "RESET ROLE");
        }

        Assert.Equal(1, await CountAsync(conn, "SELECT count(*) FROM person_key WHERE wrapped_key IS NULL AND destroyed_at IS NOT NULL"));
        Assert.Equal("23001", await SqlStateAsync(conn, "UPDATE person_key SET wrapped_key = '\\x01', destroyed_at = NULL"));
    }

    [Fact]
    public async Task Only_one_embedding_profile_is_active()
    {
        var ds = await MigratedAsync();
        await using var conn = await ds.OpenConnectionAsync(Ct);
        await ExecAsync(conn, "INSERT INTO embedding_profile (provider_id, model_id, dimensions, active) VALUES ('p', 'm1', 768, true)");
        await ExecAsync(conn, "INSERT INTO embedding_profile (provider_id, model_id, dimensions) VALUES ('p', 'm2', 1024)");

        Assert.Equal("23505", await SqlStateAsync(conn, "INSERT INTO embedding_profile (provider_id, model_id, dimensions, active) VALUES ('p', 'm3', 512, true)"));
        Assert.Equal("23514", await SqlStateAsync(conn, "INSERT INTO embedding_profile (provider_id, model_id, dimensions) VALUES ('p', 'm4', 0)"));
    }

    [Fact]
    public async Task Gc_member_is_insert_only_and_the_current_row_has_the_highest_joined_seq()
    {
        var ds = await MigratedAsync();
        await using var conn = await ds.OpenConnectionAsync(Ct);
        await ExecAsync(conn, "SET ROLE bazaar_app");
        try
        {
            await ExecAsync(conn, $"INSERT INTO gc_member (gc_id, principal_id, joined_seq) VALUES ('{_gcId}', '{_who}', 1)");
            await ExecAsync(conn, $"INSERT INTO gc_member (gc_id, principal_id, role, joined_seq) VALUES ('{_gcId}', '{_who}', 'admin', 4)");
            await ExecAsync(conn, $"INSERT INTO gc_member (gc_id, principal_id, joined_seq, left_seq) VALUES ('{_gcId}', '{_who}', 7, 7)");
            await ExecAsync(conn, $"INSERT INTO gc_member (gc_id, principal_id, joined_seq) VALUES ('{_gcId}', '{_other}', 2)");

            Assert.Equal("23505", await SqlStateAsync(conn, $"INSERT INTO gc_member (gc_id, principal_id, joined_seq) VALUES ('{_gcId}', '{_who}', 4)"));
            Assert.Equal("42501", await SqlStateAsync(conn, "UPDATE gc_member SET left_seq = 9"));
            Assert.Equal("42501", await SqlStateAsync(conn, "DELETE FROM gc_member"));
        }
        finally
        {
            await ExecAsync(conn, "RESET ROLE");
        }

        Assert.Equal("23001", await SqlStateAsync(conn, "UPDATE gc_member SET role = 'x'"));
        Assert.Equal("23001", await SqlStateAsync(conn, "DELETE FROM gc_member"));
        Assert.Equal("23001", await SqlStateAsync(conn, "TRUNCATE gc_member"));
        Assert.Equal(4, await CountAsync(conn, "SELECT count(*) FROM gc_member"));
        Assert.Equal(2, await CountAsync(conn, "SELECT count(*) FROM gc_member_current"));
        Assert.Equal(7, await CountAsync(conn, $"SELECT joined_seq FROM gc_member_current WHERE principal_id = '{_who}' AND left_seq = 7"));
    }
}
