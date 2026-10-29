using Bazaar.Server.Data.Migrations;
using Bazaar.Server.Skimmer.Data;
using Bazaar.Testing.Postgres;
using Npgsql;

namespace Bazaar.Server.Tests.Skimmer;

/// <summary>Migration 0016 on real PostgreSQL: it applies, re-applies as a no-op, and its constraints reject bad rows (doc 13, WP1).</summary>
public sealed class SkimmerSchemaTests(SkimmerDb db) : IClassFixture<SkimmerDb>
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static async Task<string> SqlStateAsync(Func<Task> action)
    {
        var ex = await Assert.ThrowsAsync<PostgresException>(action);
        return ex.SqlState;
    }

    private static NewWakeDecision Decision(Guid gcId, long seq, Guid agentId, string promptVersion = "rules-1", string path = "mention", string decision = "wake") =>
        new(Guid.CreateVersion7(), gcId, seq, agentId, path, decision, Shadow: true, ScoreBp: null, Category: null, ModelId: null, PromptVersion: promptVersion);

    [Fact]
    public async Task Migration_0016_creates_the_three_gate_tables_and_is_recorded()
    {
        Assert.SkipWhen(PgSource.IsUnsupportedHost, "Windows without BAZAAR_TEST_PG: no test PostgreSQL available.");
        await using var tables = db.DataSource.CreateCommand("""
            SELECT table_name FROM information_schema.tables
            WHERE table_schema = current_schema() AND table_name IN ('gate_config', 'wake_decisions', 'gate_labels')
            ORDER BY table_name
            """);
        await using var reader = await tables.ExecuteReaderAsync(Ct);
        var names = new List<string>();
        while (await reader.ReadAsync(Ct))
        {
            names.Add(reader.GetString(0));
        }

        Assert.Equal(["gate_config", "gate_labels", "wake_decisions"], names);

        await using var recorded = db.DataSource.CreateCommand("SELECT count(*) FROM schema_migrations WHERE version = 16 AND provider = 'common'");
        Assert.Equal(1L, await recorded.ExecuteScalarAsync(Ct));
    }

    [Fact]
    public async Task Reapplying_the_migrations_is_a_no_op()
    {
        Assert.SkipWhen(PgSource.IsUnsupportedHost, "Windows without BAZAAR_TEST_PG: no test PostgreSQL available.");
        await using var before = db.DataSource.CreateCommand("SELECT count(*) FROM schema_migrations");
        var appliedBefore = (long)(await before.ExecuteScalarAsync(Ct))!;

        var result = await new MigrationRunner(db.DataSource, new MigrationOptions(16))
            .MigrateAsync(new EmbeddedMigrationSource(typeof(MigrationRunner).Assembly), "linux", Ct);

        Assert.Empty(result.Applied);
        Assert.Equal(appliedBefore, result.AlreadyApplied);
    }

    [Fact]
    public async Task Gate_config_has_only_the_agent_and_subscriptions_columns()
    {
        Assert.SkipWhen(PgSource.IsUnsupportedHost, "Windows without BAZAAR_TEST_PG: no test PostgreSQL available.");
        // Mode, thresholds and caps are settings (doc 13, "MOVED TO SETTINGS"), so they must not be columns here.
        await using var cmd = db.DataSource.CreateCommand("""
            SELECT column_name FROM information_schema.columns
            WHERE table_schema = current_schema() AND table_name = 'gate_config'
            ORDER BY ordinal_position
            """);
        await using var reader = await cmd.ExecuteReaderAsync(Ct);
        var columns = new List<string>();
        while (await reader.ReadAsync(Ct))
        {
            columns.Add(reader.GetString(0));
        }

        Assert.Equal(["agent_id", "subscriptions"], columns);
    }

    [Fact]
    public async Task Gate_config_subscriptions_default_to_an_empty_array_so_nothing_is_scored()
    {
        Assert.SkipWhen(PgSource.IsUnsupportedHost, "Windows without BAZAAR_TEST_PG: no test PostgreSQL available.");
        var agentId = await db.CreateAgentAsync();
        await using var insert = db.DataSource.CreateCommand("INSERT INTO gate_config (agent_id) VALUES ($1)");
        insert.Parameters.AddWithValue(agentId);
        await insert.ExecuteNonQueryAsync(Ct);

        await using var read = db.DataSource.CreateCommand("SELECT subscriptions::text FROM gate_config WHERE agent_id = $1");
        read.Parameters.AddWithValue(agentId);
        Assert.Equal("[]", await read.ExecuteScalarAsync(Ct));
    }

    [Fact]
    public async Task Gate_config_needs_an_existing_agent()
    {
        Assert.SkipWhen(PgSource.IsUnsupportedHost, "Windows without BAZAAR_TEST_PG: no test PostgreSQL available.");
        var repo = new GateConfigRepository(db.DataSource);
        var orphan = new GateConfig(Guid.CreateVersion7(), "[]");
        Assert.Equal("23503", await SqlStateAsync(() => repo.UpsertAsync(orphan, ct: Ct)));
    }

    [Theory]
    [InlineData("unknown", "wake", "23514")]
    [InlineData("mention", "promote", "23514")]
    [InlineData("scored", "promote", "23514")]
    public async Task Wake_decision_rejects_unknown_paths_and_decisions(string path, string decision, string expected)
    {
        Assert.SkipWhen(PgSource.IsUnsupportedHost, "Windows without BAZAAR_TEST_PG: no test PostgreSQL available.");
        var agentId = await db.CreateAgentAsync();
        var (gcId, seq) = await db.CreateGcEventAsync();
        var repo = new WakeDecisionRepository(db.DataSource);
        Assert.Equal(expected, await SqlStateAsync(() => repo.InsertAsync(Decision(gcId, seq, agentId, path: path, decision: decision), ct: Ct)));
    }

    [Theory]
    [InlineData("Question")]
    [InlineData("gossip")]
    public async Task Wake_decision_rejects_categories_outside_the_closed_set(string category)
    {
        Assert.SkipWhen(PgSource.IsUnsupportedHost, "Windows without BAZAAR_TEST_PG: no test PostgreSQL available.");
        var agentId = await db.CreateAgentAsync();
        var (gcId, seq) = await db.CreateGcEventAsync();
        var repo = new WakeDecisionRepository(db.DataSource);
        var bad = Decision(gcId, seq, agentId) with { Category = category };
        Assert.Equal("23514", await SqlStateAsync(() => repo.InsertAsync(bad, ct: Ct)));
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(10001)]
    public async Task Wake_decision_score_must_be_basis_points(int scoreBp)
    {
        Assert.SkipWhen(PgSource.IsUnsupportedHost, "Windows without BAZAAR_TEST_PG: no test PostgreSQL available.");
        var agentId = await db.CreateAgentAsync();
        var (gcId, seq) = await db.CreateGcEventAsync();
        var repo = new WakeDecisionRepository(db.DataSource);
        var bad = Decision(gcId, seq, agentId, path: "scored") with { ScoreBp = scoreBp };
        Assert.Equal("23514", await SqlStateAsync(() => repo.InsertAsync(bad, ct: Ct)));
    }

    [Fact]
    public async Task Wake_decision_seq_must_be_positive()
    {
        Assert.SkipWhen(PgSource.IsUnsupportedHost, "Windows without BAZAAR_TEST_PG: no test PostgreSQL available.");
        var agentId = await db.CreateAgentAsync();
        var (gcId, _) = await db.CreateGcEventAsync();
        var repo = new WakeDecisionRepository(db.DataSource);
        Assert.Equal("23514", await SqlStateAsync(() => repo.InsertAsync(Decision(gcId, 0, agentId), ct: Ct)));
    }

    [Fact]
    public async Task Wake_decision_needs_an_existing_message_and_agent()
    {
        Assert.SkipWhen(PgSource.IsUnsupportedHost, "Windows without BAZAAR_TEST_PG: no test PostgreSQL available.");
        var agentId = await db.CreateAgentAsync();
        var (gcId, seq) = await db.CreateGcEventAsync();
        var repo = new WakeDecisionRepository(db.DataSource);

        Assert.Equal("23503", await SqlStateAsync(() => repo.InsertAsync(Decision(gcId, seq + 1, agentId), ct: Ct)));
        Assert.Equal("23503", await SqlStateAsync(() => repo.InsertAsync(Decision(gcId, seq, Guid.CreateVersion7()), ct: Ct)));
    }

    [Fact]
    public async Task Wake_decision_key_is_unique_per_prompt_version()
    {
        Assert.SkipWhen(PgSource.IsUnsupportedHost, "Windows without BAZAAR_TEST_PG: no test PostgreSQL available.");
        var agentId = await db.CreateAgentAsync();
        var (gcId, seq) = await db.CreateGcEventAsync();
        var repo = new WakeDecisionRepository(db.DataSource);

        Assert.True(await repo.InsertAsync(Decision(gcId, seq, agentId, "rules-1"), ct: Ct));
        Assert.False(await repo.InsertAsync(Decision(gcId, seq, agentId, "rules-1"), ct: Ct));
        Assert.True(await repo.InsertAsync(Decision(gcId, seq, agentId, "rules-2"), ct: Ct));
    }

    [Fact]
    public async Task Gate_label_rejects_unknown_sources_blank_labels_and_missing_decisions()
    {
        Assert.SkipWhen(PgSource.IsUnsupportedHost, "Windows without BAZAAR_TEST_PG: no test PostgreSQL available.");
        var agentId = await db.CreateAgentAsync();
        var (gcId, seq) = await db.CreateGcEventAsync();
        var decisionId = Guid.CreateVersion7();
        await new WakeDecisionRepository(db.DataSource).InsertAsync(Decision(gcId, seq, agentId) with { Id = decisionId }, ct: Ct);
        var labels = new GateLabelRepository(db.DataSource);

        Assert.Equal("23514", await SqlStateAsync(() => labels.InsertAsync(new NewGateLabel(Guid.CreateVersion7(), decisionId, "should_wake", "guess", Guid.CreateVersion7()), ct: Ct)));
        Assert.Equal("23514", await SqlStateAsync(() => labels.InsertAsync(new NewGateLabel(Guid.CreateVersion7(), decisionId, "  ", "agent_flag", Guid.CreateVersion7()), ct: Ct)));
        Assert.Equal("23503", await SqlStateAsync(() => labels.InsertAsync(new NewGateLabel(Guid.CreateVersion7(), Guid.CreateVersion7(), "should_wake", "agent_flag", Guid.CreateVersion7()), ct: Ct)));
    }
}
