using Bazaar.Server.Security.Audit;
using Bazaar.Server.Security.Audit.Db;
using Bazaar.Testing.Postgres;
using Npgsql;

namespace Bazaar.Server.Tests.Security.Audit.Db;

/// <summary>The runtime role (bazaar_app) can append and read but cannot rewrite the log (doc 04, doc 15).</summary>
public sealed class AuditRoleTests
{
    private static readonly DateTimeOffset _t0 = new(2026, 10, 25, 12, 0, 0, TimeSpan.Zero);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static void SkipWithoutDatabase() =>
        Assert.SkipWhen(PgSource.IsUnsupportedHost, "Windows without BAZAAR_TEST_PG: no test PostgreSQL available.");

    private static async Task<string> ExpectDeniedAsync(NpgsqlDataSource ds, string sql)
    {
        var ex = await Assert.ThrowsAsync<PostgresException>(async () =>
        {
            await using var cmd = ds.CreateCommand(sql);
            await cmd.ExecuteNonQueryAsync(Ct);
        });
        Assert.Equal("42501", ex.SqlState); // insufficient_privilege
        return ex.MessageText;
    }

    private static async Task<long> CountAsync(NpgsqlDataSource ds)
    {
        await using var cmd = ds.CreateCommand("SELECT count(*) FROM audit_log");
        return (long)(await cmd.ExecuteScalarAsync(Ct))!;
    }

    [Fact]
    public async Task Runtime_role_can_append_and_read()
    {
        SkipWithoutDatabase();
        await using var db = await AuditTestDb.CreateAsync(Ct);
        await using var app = db.AsRole("bazaar_app");
        Assert.Equal("bazaar_app", await ScalarTextAsync(app, "SELECT current_user"));

        var log = new PostgresAuditLog(app);
        await log.AppendAsync(new AuditEntry(_t0, "agent:charles", "tool.call", "{}"), Ct);
        await log.AppendAsync(new AuditEntry(_t0.AddSeconds(1), "owner", "config.change", "{\"k\": 1}"), Ct);

        Assert.Equal(2, await CountAsync(app));
        Assert.True((await log.VerifyAsync(ct: Ct)).IsValid);
    }

    [Fact]
    public async Task Runtime_role_cannot_update_delete_or_truncate()
    {
        SkipWithoutDatabase();
        await using var db = await AuditTestDb.CreateAsync(Ct);
        await new PostgresAuditLog(db.DataSource).AppendAsync(new AuditEntry(_t0, "agent:charles", "tool.call", "{}"), Ct);
        await using var app = db.AsRole("bazaar_app");

        var updateMessage = await ExpectDeniedAsync(app, "UPDATE audit_log SET actor = 'nobody'");
        Assert.Contains("permission denied", updateMessage, StringComparison.OrdinalIgnoreCase);
        await ExpectDeniedAsync(app, "DELETE FROM audit_log");
        await ExpectDeniedAsync(app, "TRUNCATE audit_log");

        // Nothing changed: the refused statements had no effect.
        await using var check = db.DataSource.CreateCommand("SELECT actor FROM audit_log");
        Assert.Equal("agent:charles", (string)(await check.ExecuteScalarAsync(Ct))!);
    }

    [Fact]
    public async Task Runtime_role_privileges_are_exactly_insert_and_select()
    {
        SkipWithoutDatabase();
        await using var db = await AuditTestDb.CreateAsync(Ct);

        Assert.True(await HasPrivilegeAsync(db.DataSource, "INSERT"));
        Assert.True(await HasPrivilegeAsync(db.DataSource, "SELECT"));
        Assert.False(await HasPrivilegeAsync(db.DataSource, "UPDATE"));
        Assert.False(await HasPrivilegeAsync(db.DataSource, "DELETE"));
        Assert.False(await HasPrivilegeAsync(db.DataSource, "TRUNCATE"));
    }

    [Fact]
    public async Task Superuser_update_and_delete_are_refused_by_the_trigger()
    {
        SkipWithoutDatabase();
        await using var db = await AuditTestDb.CreateAsync(Ct);
        await new PostgresAuditLog(db.DataSource).AppendAsync(new AuditEntry(_t0, "agent:charles", "tool.call", "{}"), Ct);

        // Superusers bypass privileges, not triggers: the defence in depth still refuses.
        var updateMessage = await ExpectDeniedAsync(db.DataSource, "UPDATE audit_log SET actor = 'nobody'");
        Assert.Contains("append-only", updateMessage, StringComparison.Ordinal);
        await ExpectDeniedAsync(db.DataSource, "DELETE FROM audit_log");
        await ExpectDeniedAsync(db.DataSource, "TRUNCATE audit_log");
        Assert.Equal(1, await CountAsync(db.DataSource));
    }

    private static async Task<bool> HasPrivilegeAsync(NpgsqlDataSource ds, string privilege)
    {
        await using var cmd = ds.CreateCommand($"SELECT has_table_privilege('bazaar_app', 'audit_log', '{privilege}')");
        return (bool)(await cmd.ExecuteScalarAsync(Ct))!;
    }

    private static async Task<string> ScalarTextAsync(NpgsqlDataSource ds, string sql)
    {
        await using var cmd = ds.CreateCommand(sql);
        return (string)(await cmd.ExecuteScalarAsync(Ct))!;
    }
}
