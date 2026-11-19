using Bazaar.Server.Modules.LineMove;
using Bazaar.Server.Modules.Persistence;
using Bazaar.Server.Modules.Updates;
using Bazaar.Server.Security.Audit.Db;
using Npgsql;

namespace Bazaar.Server.Modules.Tests.Persistence;

/// <summary><see cref="ModuleManagerRepository"/> against the real schema (<c>modules/0001</c>). Needs a test PostgreSQL.</summary>
public sealed class ModuleManagerRepositoryTests(ModuleDb db) : IClassFixture<ModuleDb>
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static readonly DateTimeOffset _t0 = new(2026, 11, 1, 10, 0, 0, TimeSpan.Zero);

    private ModuleManagerRepository Repo => new(db.DataSource, new PostgresAuditLog(db.DataSource));

    /// <summary>A module id no other test in this class uses: the class shares one database.</summary>
    private static string UniqueModule() => $"mod-{Guid.NewGuid():N}";

    private static ModuleUpdateDecisionRecord Decision(string moduleId, UpdateDecision decision, DateTimeOffset at, string? policyId = null) =>
        new(moduleId, "18.1.2", "18", decision, "because", policyId, at, "evaluator");

    private static LineMoveAuditEvent MoveEvent(string moveId, LineMoveStep step, LineMoveAuditKind kind, DateTimeOffset at, string? snapshot = null) =>
        new(moveId, step, kind, 17, 18, "owner", snapshot, $"{step} detail", at);

    private async Task<string> SqlStateOfAsync(string sql, params object[] args)
    {
        await using var cmd = db.DataSource.CreateCommand(sql);
        foreach (var a in args)
            cmd.Parameters.Add(new NpgsqlParameter { Value = a });
        var ex = await Assert.ThrowsAsync<PostgresException>(() => cmd.ExecuteNonQueryAsync(Ct));
        return ex.SqlState;
    }

    [Fact]
    public async Task A_module_with_no_decisions_lists_none()
    {
        ModuleTestSkip.WithoutDatabase();

        Assert.Empty(await Repo.ListDecisionsAsync(UniqueModule(), 10, Ct));
    }

    [Fact]
    public async Task Decisions_are_kept_and_listed_newest_first()
    {
        ModuleTestSkip.WithoutDatabase();
        var module = UniqueModule();
        await Repo.AppendDecisionAsync(Decision(module, UpdateDecision.Manual, _t0), Ct);
        await Repo.AppendDecisionAsync(Decision(module, UpdateDecision.Auto, _t0.AddHours(1), "policy-1"), Ct);
        await Repo.AppendDecisionAsync(Decision(module, UpdateDecision.Deferred, _t0.AddHours(2)), Ct);

        var listed = await Repo.ListDecisionsAsync(module, 10, Ct);

        Assert.Equal([UpdateDecision.Deferred, UpdateDecision.Auto, UpdateDecision.Manual], listed.Select(d => d.Decision));
        Assert.Equal("policy-1", listed[1].PolicyId);
        Assert.Null(listed[0].PolicyId);
    }

    [Fact]
    public async Task A_decision_reads_back_with_every_field()
    {
        ModuleTestSkip.WithoutDatabase();
        var module = UniqueModule();
        var written = Decision(module, UpdateDecision.AwaitingApproval, _t0, "policy-x");

        await Repo.AppendDecisionAsync(written, Ct);

        Assert.Equal(written, Assert.Single(await Repo.ListDecisionsAsync(module, 5, Ct)));
    }

    [Fact]
    public async Task The_decision_is_stored_under_its_enum_name()
    {
        ModuleTestSkip.WithoutDatabase();
        var module = UniqueModule();

        await Repo.AppendDecisionAsync(Decision(module, UpdateDecision.Blocked, _t0), Ct);

        Assert.Equal("Blocked", await db.ScalarAsync<string>("SELECT decision FROM module_update_decision WHERE module_id = $1", module));
    }

    [Fact]
    public async Task The_limit_caps_the_page()
    {
        ModuleTestSkip.WithoutDatabase();
        var module = UniqueModule();
        for (var i = 0; i < 4; i++)
            await Repo.AppendDecisionAsync(Decision(module, UpdateDecision.Manual, _t0.AddMinutes(i)), Ct);

        var page = await Repo.ListDecisionsAsync(module, 2, Ct);

        Assert.Equal([_t0.AddMinutes(3), _t0.AddMinutes(2)], page.Select(d => d.DecidedAt));
    }

    [Fact]
    public async Task Decisions_of_one_module_do_not_show_in_another()
    {
        ModuleTestSkip.WithoutDatabase();
        var first = UniqueModule();
        var second = UniqueModule();
        await Repo.AppendDecisionAsync(Decision(first, UpdateDecision.Pinned, _t0), Ct);

        Assert.Empty(await Repo.ListDecisionsAsync(second, 10, Ct));
    }

    [Fact]
    public async Task A_decision_outside_the_enum_is_refused_by_the_check()
    {
        ModuleTestSkip.WithoutDatabase();

        var state = await SqlStateOfAsync(
            "INSERT INTO module_update_decision (module_id, version, line, decision, reason, decided_at, actor) VALUES ('m', '1', '1', 'Maybe', 'r', now(), 'a')");

        Assert.Equal("23514", state);
    }

    [Fact]
    public async Task Update_and_delete_of_a_decision_are_refused()
    {
        ModuleTestSkip.WithoutDatabase();
        var module = UniqueModule();
        await Repo.AppendDecisionAsync(Decision(module, UpdateDecision.Manual, _t0), Ct);

        Assert.Equal("P0001", await SqlStateOfAsync("UPDATE module_update_decision SET reason = 'changed' WHERE module_id = $1", module));
        Assert.Equal("P0001", await SqlStateOfAsync("DELETE FROM module_update_decision WHERE module_id = $1", module));
        Assert.Equal(1L, await db.ScalarAsync<long>("SELECT count(*) FROM module_update_decision WHERE module_id = $1", module));
    }

    [Fact]
    public async Task A_line_move_lists_its_events_in_the_order_they_were_written()
    {
        ModuleTestSkip.WithoutDatabase();
        var move = UniqueModule();
        await Repo.AppendLineMoveEventAsync(MoveEvent(move, LineMoveStep.Preflight, LineMoveAuditKind.Succeeded, _t0), Ct);
        await Repo.AppendLineMoveEventAsync(MoveEvent(move, LineMoveStep.Snapshot, LineMoveAuditKind.Succeeded, _t0.AddSeconds(5), "snap-1"), Ct);
        await Repo.AppendLineMoveEventAsync(MoveEvent(move, LineMoveStep.Switch, LineMoveAuditKind.Failed, _t0.AddSeconds(9)), Ct);

        var events = await Repo.ListLineMoveEventsAsync(move, Ct);

        Assert.Equal([LineMoveStep.Preflight, LineMoveStep.Snapshot, LineMoveStep.Switch], events.Select(e => e.Step));
        Assert.Equal([LineMoveAuditKind.Succeeded, LineMoveAuditKind.Succeeded, LineMoveAuditKind.Failed], events.Select(e => e.Kind));
        Assert.Null(events[0].SnapshotId);
        Assert.Equal("snap-1", events[1].SnapshotId);
    }

    [Fact]
    public async Task A_line_move_event_reads_back_with_every_field()
    {
        ModuleTestSkip.WithoutDatabase();
        var move = UniqueModule();
        var written = MoveEvent(move, LineMoveStep.RolledBack, LineMoveAuditKind.ManualRollbackRequired, _t0, "snap-9");

        await Repo.AppendLineMoveEventAsync(written, Ct);

        Assert.Equal(written, Assert.Single(await Repo.ListLineMoveEventsAsync(move, Ct)));
    }

    [Fact]
    public async Task A_line_move_step_outside_the_enum_is_refused_by_the_check()
    {
        ModuleTestSkip.WithoutDatabase();

        var state = await SqlStateOfAsync(
            "INSERT INTO module_line_move_event (move_id, step, outcome, from_line, to_line, approving_role, occurred_at, detail) VALUES ('m', 'Teleport', 'Succeeded', '17', '18', 'owner', now(), 'd')");

        Assert.Equal("23514", state);
    }

    [Fact]
    public async Task Update_and_delete_of_a_line_move_event_are_refused()
    {
        ModuleTestSkip.WithoutDatabase();
        var move = UniqueModule();
        await Repo.AppendLineMoveEventAsync(MoveEvent(move, LineMoveStep.Verify, LineMoveAuditKind.Succeeded, _t0), Ct);

        Assert.Equal("P0001", await SqlStateOfAsync("UPDATE module_line_move_event SET detail = 'x' WHERE move_id = $1", move));
        Assert.Equal("P0001", await SqlStateOfAsync("DELETE FROM module_line_move_event WHERE move_id = $1", move));
        Assert.Equal("P0001", await SqlStateOfAsync("TRUNCATE module_line_move_event"));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(ModuleManagerRepository.MaxListLimit + 1)]
    public async Task A_limit_outside_the_range_is_refused_before_the_database_is_used(int limit)
    {
        // No server is contacted: the argument check runs first, so an unopened source is enough.
        await using var source = NpgsqlDataSource.Create("Host=localhost;Database=unused");

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => new ModuleManagerRepository(source, new PostgresAuditLog(source)).ListDecisionsAsync("m", limit, Ct));
    }
}
