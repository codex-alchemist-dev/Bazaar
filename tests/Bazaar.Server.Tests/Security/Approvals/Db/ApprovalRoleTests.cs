using System.Collections.Immutable;
using Bazaar.Server.Security.Approvals;
using Bazaar.Server.Security.Approvals.Db;
using Bazaar.Server.Security.Taint;
using Npgsql;

namespace Bazaar.Server.Tests.Security.Approvals.Db;

/// <summary>The runtime role (bazaar_app) can store and move approvals but cannot delete them (doc 15, migration 0011).</summary>
public sealed class ApprovalRoleTests
{

    private static readonly DateTimeOffset _t0 = new(2026, 10, 26, 9, 0, 0, TimeSpan.Zero);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static async Task<bool> HasPrivilegeAsync(NpgsqlDataSource ds, string privilege)
    {
        await using var cmd = ds.CreateCommand($"SELECT has_table_privilege('bazaar_app', 'approval', '{privilege}')");
        return (bool)(await cmd.ExecuteScalarAsync(Ct))!;
    }

    [Fact]
    public async Task Runtime_role_privileges_are_insert_select_and_update_but_not_delete()
    {
        await using var db = await ApprovalTestDb.CreateAsync(Ct);

        Assert.True(await HasPrivilegeAsync(db.DataSource, "INSERT"));
        Assert.True(await HasPrivilegeAsync(db.DataSource, "SELECT"));
        Assert.True(await HasPrivilegeAsync(db.DataSource, "UPDATE"));
        Assert.False(await HasPrivilegeAsync(db.DataSource, "DELETE"));
        Assert.False(await HasPrivilegeAsync(db.DataSource, "TRUNCATE"));
    }

    [Fact]
    public async Task Runtime_role_cannot_delete_an_approval_row()
    {
        await using var db = await ApprovalTestDb.CreateAsync(Ct);
        var id = Guid.NewGuid();
        await new PostgresApprovalStore(db.DataSource).InsertAsync(new ApprovalRequest(
            Id: id,
            AgentId: "agent:charles",
            SessionId: Guid.NewGuid(),
            Tool: "github.merge",
            Target: null,
            ArgsHash: new byte[32],
            PayloadJson: "{}",
            TaintSources: ImmutableArray<TaintSource>.Empty,
            Status: ApprovalStatus.Pending,
            RequestedAt: _t0,
            ExpiresAt: _t0.AddHours(24)), Ct);

        await using var app = db.AsRole("bazaar_app");
        Assert.Equal("bazaar_app", await ScalarTextAsync(app, "SELECT current_user"));

        var ex = await Assert.ThrowsAsync<PostgresException>(async () =>
        {
            await using var cmd = app.CreateCommand("DELETE FROM approval");
            await cmd.ExecuteNonQueryAsync(Ct);
        });
        Assert.Equal("42501", ex.SqlState); // insufficient_privilege

        // The refused statement had no effect, and the runtime role can still read the row through the store.
        Assert.NotNull(await new PostgresApprovalStore(app).GetAsync(id, Ct));
    }

    private static async Task<string> ScalarTextAsync(NpgsqlDataSource ds, string sql)
    {
        await using var cmd = ds.CreateCommand(sql);
        return (string)(await cmd.ExecuteScalarAsync(Ct))!;
    }
}
