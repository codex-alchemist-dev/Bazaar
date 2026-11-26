using Bazaar.Server.Data.Migrations;
using Bazaar.Server.Tests.Data.Migrations;
using Bazaar.Testing.Postgres;
using Npgsql;

namespace Bazaar.Server.Tests.Data;

/// <summary>Migration <c>common/0019_identity_keys.sql</c> on real PostgreSQL (doc 14, data and interfaces).</summary>
public sealed class IdentityKeysSchemaTests(MigrationDb db) : IClassFixture<MigrationDb>
{
    private static readonly EmbeddedMigrationSource _source = new(typeof(MigrationRunner).Assembly);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private async Task<NpgsqlDataSource> MigratedAsync()
    {
        Assert.SkipWhen(PgSource.IsUnsupportedHost, "Windows without BAZAAR_TEST_PG: no test PostgreSQL available.");
        await using (var reset = db.DataSource.CreateCommand("DROP SCHEMA public CASCADE; CREATE SCHEMA public"))
            await reset.ExecuteNonQueryAsync(Ct);
        await new MigrationRunner(db.DataSource, new MigrationOptions(4)).MigrateAsync(_source, "linux", Ct);
        return db.DataSource;
    }

    private static async Task ExecAsync(NpgsqlConnection conn, string sql)
    {
        await using var cmd = new NpgsqlCommand(sql, conn);
        await cmd.ExecuteNonQueryAsync(Ct);
    }

    private static async Task<string> SqlStateAsync(NpgsqlConnection conn, string sql) =>
        (await Assert.ThrowsAsync<PostgresException>(() => ExecAsync(conn, sql))).SqlState;

    private const string _dek = "'0197a000-0000-7000-8000-0000000000d1'";

    private static string InsertDek(string dekId, string scope, string? retired = null) =>
        $"INSERT INTO soul_dek (dek_id, scope, wrapped_dek, kek_id, retired_at) VALUES ('{dekId}', '{scope}', '\\xdead'::bytea, 'kek-1', {retired ?? "NULL"})";

    [Fact]
    public async Task Only_one_live_dek_per_scope()
    {
        var ds = await MigratedAsync();
        await using var conn = await ds.OpenConnectionAsync(Ct);

        await ExecAsync(conn, InsertDek("0197a000-0000-7000-8000-0000000000d1", "install"));

        Assert.Equal("23505", await SqlStateAsync(conn, InsertDek("0197a000-0000-7000-8000-0000000000d2", "install")));
        await ExecAsync(conn, InsertDek("0197a000-0000-7000-8000-0000000000d3", "soul-2", "now()"));
        await ExecAsync(conn, InsertDek("0197a000-0000-7000-8000-0000000000d4", "soul-2"));
    }

    [Fact]
    public async Task A_live_dek_must_keep_its_wrapped_key()
    {
        var ds = await MigratedAsync();
        await using var conn = await ds.OpenConnectionAsync(Ct);

        Assert.Equal("23514", await SqlStateAsync(conn,
            "INSERT INTO soul_dek (dek_id, scope, wrapped_dek, kek_id) VALUES ('0197a000-0000-7000-8000-0000000000d5', 'x', NULL, 'kek-1')"));
    }

    [Fact]
    public async Task Credential_rejects_unknown_kind_and_bad_nonce()
    {
        var ds = await MigratedAsync();
        await using var conn = await ds.OpenConnectionAsync(Ct);
        await ExecAsync(conn, InsertDek("0197a000-0000-7000-8000-0000000000d1", "install"));

        const string head = "INSERT INTO credential (credential_id, service, kind, handle, ciphertext, nonce, dek_id, tier, status) VALUES (";
        var ok = $"{head}'0197a000-0000-7000-8000-0000000000c1', 'github', 'app_key', 'h1', '\\x01'::bytea, '\\x000000000000000000000000'::bytea, {_dek}, 'sensitive', 'active')";
        await ExecAsync(conn, ok);

        Assert.Equal("23514", await SqlStateAsync(conn,
            $"{head}'0197a000-0000-7000-8000-0000000000c2', 'github', 'password', 'h2', '\\x01'::bytea, '\\x000000000000000000000000'::bytea, {_dek}, 'sensitive', 'active')"));
        Assert.Equal("23514", await SqlStateAsync(conn,
            $"{head}'0197a000-0000-7000-8000-0000000000c3', 'github', 'app_key', 'h3', '\\x01'::bytea, '\\x0000'::bytea, {_dek}, 'sensitive', 'active')"));
    }

    [Fact]
    public async Task Credential_handle_is_unique_while_active()
    {
        var ds = await MigratedAsync();
        await using var conn = await ds.OpenConnectionAsync(Ct);
        await ExecAsync(conn, InsertDek("0197a000-0000-7000-8000-0000000000d1", "install"));

        const string insert = "INSERT INTO credential (credential_id, service, kind, handle, ciphertext, nonce, dek_id, tier, status) VALUES (";
        var tail = $", 'github', 'app_key', 'same', '\\x01'::bytea, '\\x000000000000000000000000'::bytea, {_dek}, 'ordinary', ";
        await ExecAsync(conn, $"{insert}'0197a000-0000-7000-8000-0000000000c1'{tail}'rotated')");
        await ExecAsync(conn, $"{insert}'0197a000-0000-7000-8000-0000000000c2'{tail}'active')");
        Assert.Equal("23505", await SqlStateAsync(conn, $"{insert}'0197a000-0000-7000-8000-0000000000c3'{tail}'active')"));
    }

    [Fact]
    public async Task Browser_profile_allows_one_anonymous_profile_per_agent()
    {
        var ds = await MigratedAsync();
        await using var conn = await ds.OpenConnectionAsync(Ct);
        await ExecAsync(conn, InsertDek("0197a000-0000-7000-8000-0000000000d1", "install"));

        const string insert = "INSERT INTO browser_profile (profile_id, agent_id, profile_kind, ciphertext, nonce, dek_id) VALUES (";
        var agent = "'0197a000-0000-7000-8000-0000000000a1'";
        await ExecAsync(conn, $"{insert}'0197a000-0000-7000-8000-0000000000b1', {agent}, 'anonymous', '\\x01'::bytea, '\\x000000000000000000000000'::bytea, {_dek})");
        await ExecAsync(conn, $"{insert}'0197a000-0000-7000-8000-0000000000b2', {agent}, 'account', '\\x01'::bytea, '\\x000000000000000000000000'::bytea, {_dek})");
        Assert.Equal("23505", await SqlStateAsync(conn,
            $"{insert}'0197a000-0000-7000-8000-0000000000b3', {agent}, 'anonymous', '\\x01'::bytea, '\\x000000000000000000000000'::bytea, {_dek})"));
    }

    [Fact]
    public async Task Lease_must_expire_after_it_is_issued()
    {
        var ds = await MigratedAsync();
        await using var conn = await ds.OpenConnectionAsync(Ct);
        await ExecAsync(conn, InsertDek("0197a000-0000-7000-8000-0000000000d1", "install"));
        await ExecAsync(conn, "INSERT INTO credential (credential_id, service, kind, handle, ciphertext, nonce, dek_id, tier, status) VALUES ('0197a000-0000-7000-8000-0000000000c1', 'github', 'app_key', 'h1', '\\x01'::bytea, '\\x000000000000000000000000'::bytea, " + _dek + ", 'ordinary', 'active')");

        Assert.Equal("23514", await SqlStateAsync(conn,
            "INSERT INTO credential_lease (lease_id, agent_id, credential_id, scope, issued_at, expires_at) VALUES ('0197a000-0000-7000-8000-0000000000e1', '0197a000-0000-7000-8000-0000000000a1', '0197a000-0000-7000-8000-0000000000c1', 's', '2026-01-02', '2026-01-01')"));
    }
}
