using Bazaar.Server.Tests.Kanban.Data;
using Bazaar.Testing.Postgres;
using Npgsql;

namespace Bazaar.Server.Tests.Ryusei;

/// <summary>RyuseiFuda schedule tables (migration ryusei/0001, doc 38 WP-R3) on real PostgreSQL. Uses the Kanban fixture, which migrates every namespace.</summary>
public sealed class RyuseiTriggerMigrationTests(KanbanDb db) : IClassFixture<KanbanDb>
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static void SkipWithoutDatabase() =>
        Assert.SkipWhen(PgSource.IsUnsupportedHost, "Windows without BAZAAR_TEST_PG: no test PostgreSQL available.");

    private static async Task<string> SqlStateAsync(Func<Task> action)
    {
        var ex = await Assert.ThrowsAsync<PostgresException>(action);
        return ex.SqlState;
    }

    /// <summary>Inserts a trigger row with the given ping mode and attach GC (null for none), returning its id.</summary>
    private async Task<Guid> InsertTriggerAsync(string pingMode, Guid? attachGcId)
    {
        const string sql = """
            INSERT INTO ryusei_trigger (project_id, created_by, kind, target_agent_ids, purpose, spec, attach_gc_id, ping_mode, state)
            VALUES ($1, $2, 'timer', $3, 'check the build', '{}'::jsonb, $4, $5, 'active')
            RETURNING trigger_id
            """;
        await using var cmd = db.DataSource.CreateCommand(sql);
        cmd.Parameters.AddWithValue(Guid.CreateVersion7());
        cmd.Parameters.AddWithValue(Guid.CreateVersion7());
        cmd.Parameters.AddWithValue(new[] { Guid.CreateVersion7() });
        cmd.Parameters.AddWithValue((object?)attachGcId ?? DBNull.Value);
        cmd.Parameters.AddWithValue(pingMode);
        return (Guid)(await cmd.ExecuteScalarAsync(Ct))!;
    }

    /// <summary>Inserts a fire row for a trigger and returns its id.</summary>
    private async Task<Guid> InsertFireAsync(Guid triggerId, DateTimeOffset scheduledFor, string outcome = "woke")
    {
        await using var cmd = db.DataSource.CreateCommand(
            "INSERT INTO ryusei_fire (trigger_id, scheduled_for, outcome) VALUES ($1, $2, $3) RETURNING fire_id");
        cmd.Parameters.AddWithValue(triggerId);
        cmd.Parameters.AddWithValue(scheduledFor);
        cmd.Parameters.AddWithValue(outcome);
        return (Guid)(await cmd.ExecuteScalarAsync(Ct))!;
    }

    [Fact]
    public async Task Both_tables_exist_with_the_doc_38_columns()
    {
        SkipWithoutDatabase();

        await using var cmd = db.DataSource.CreateCommand("""
            SELECT table_name, column_name
            FROM information_schema.columns
            WHERE table_schema = current_schema() AND table_name IN ('ryusei_trigger', 'ryusei_fire')
            """);
        var columns = new HashSet<string>(StringComparer.Ordinal);
        await using var reader = await cmd.ExecuteReaderAsync(Ct);
        while (await reader.ReadAsync(Ct))
            columns.Add($"{reader.GetString(0)}.{reader.GetString(1)}");

        Assert.Contains("ryusei_trigger.trigger_id", columns);
        Assert.Contains("ryusei_trigger.attach_gc_id", columns);
        Assert.Contains("ryusei_trigger.ping_mode", columns);
        Assert.Contains("ryusei_trigger.target_agent_ids", columns);
        Assert.Contains("ryusei_fire.fire_id", columns);
        Assert.Contains("ryusei_fire.scheduled_for", columns);
        Assert.Contains("ryusei_fire.outcome", columns);
    }

    [Fact]
    public async Task Attach_gc_is_required_exactly_when_ping_mode_is_attach()
    {
        SkipWithoutDatabase();
        var (gcId, _) = await db.CreateGcWithMessageAsync();

        // attach requires a GC.
        Assert.Equal("23514", await SqlStateAsync(() => InsertTriggerAsync("attach", null)));

        // The other modes forbid a GC.
        Assert.Equal("23514", await SqlStateAsync(() => InsertTriggerAsync("none", gcId)));
        Assert.Equal("23514", await SqlStateAsync(() => InsertTriggerAsync("new_gc", gcId)));

        // The allowed combinations are accepted.
        await InsertTriggerAsync("attach", gcId);
        await InsertTriggerAsync("none", null);
        await InsertTriggerAsync("new_gc", null);
    }

    [Fact]
    public async Task Fire_outcome_is_limited_to_the_doc_38_list()
    {
        SkipWithoutDatabase();
        var triggerId = await InsertTriggerAsync("none", null);

        Assert.Equal("23514", await SqlStateAsync(() => InsertFireAsync(triggerId, DateTimeOffset.UtcNow, "unknown")));
        var outcomes = new[] { "woke", "coalesced", "refused_cap", "refused_cooldown", "refused_loop", "skipped_inflight", "skipped_resting", "error" };
        for (var i = 0; i < outcomes.Length; i++)
            await InsertFireAsync(triggerId, DateTimeOffset.UtcNow.AddSeconds(i + 1), outcomes[i]);
    }

    [Fact]
    public async Task A_fire_cannot_be_updated_deleted_or_truncated()
    {
        SkipWithoutDatabase();
        var triggerId = await InsertTriggerAsync("none", null);
        var fireId = await InsertFireAsync(triggerId, new DateTimeOffset(2026, 11, 1, 9, 0, 0, TimeSpan.Zero));

        await using var update = db.DataSource.CreateCommand("UPDATE ryusei_fire SET outcome = 'error' WHERE fire_id = $1");
        update.Parameters.AddWithValue(fireId);
        Assert.Equal("23001", await SqlStateAsync(() => update.ExecuteNonQueryAsync(Ct)));

        await using var delete = db.DataSource.CreateCommand("DELETE FROM ryusei_fire WHERE fire_id = $1");
        delete.Parameters.AddWithValue(fireId);
        Assert.Equal("23001", await SqlStateAsync(() => delete.ExecuteNonQueryAsync(Ct)));

        await using var truncate = db.DataSource.CreateCommand("TRUNCATE ryusei_fire");
        Assert.Equal("23001", await SqlStateAsync(() => truncate.ExecuteNonQueryAsync(Ct)));
    }

    [Fact]
    public async Task A_second_fire_for_the_same_trigger_and_scheduled_time_is_rejected()
    {
        SkipWithoutDatabase();
        var triggerId = await InsertTriggerAsync("none", null);
        var scheduledFor = new DateTimeOffset(2026, 11, 2, 8, 30, 0, TimeSpan.Zero);
        await InsertFireAsync(triggerId, scheduledFor);

        Assert.Equal("23505", await SqlStateAsync(() => InsertFireAsync(triggerId, scheduledFor, "coalesced")));

        // A different scheduled time for the same trigger is a new fire.
        await InsertFireAsync(triggerId, scheduledFor.AddMinutes(5));
    }
}
