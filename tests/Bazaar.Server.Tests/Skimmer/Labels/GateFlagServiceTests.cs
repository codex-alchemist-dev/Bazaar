using Bazaar.Server.Skimmer.Data;
using Bazaar.Server.Skimmer.Labels;
using Bazaar.Testing.Postgres;
using Npgsql;

namespace Bazaar.Server.Tests.Skimmer.Labels;

/// <summary>Agent flags write gate_labels rows with source agent_flag, and nothing else (doc 13, "Feedback"), on real PostgreSQL.</summary>
public sealed class GateFlagServiceTests(SkimmerDb db) : IClassFixture<SkimmerDb>
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static readonly string[] _expectedFlagLabels = ["missed", "false_wake"];

    private GateFlagService CreateService() =>
        new(new GateLabelRepository(db.DataSource), new WakeDecisionRepository(db.DataSource));

    [Fact]
    public async Task Flag_missed_writes_a_missed_label_with_agent_flag_source()
    {
        Assert.SkipWhen(PgSource.IsUnsupportedHost, "Windows without BAZAAR_TEST_PG: no test PostgreSQL available.");
        var agentId = await db.CreateAgentAsync();
        var (gcId, seq) = await db.CreateGcEventAsync();
        var decisionId = await InsertDecisionAsync(agentId, gcId, seq, "rules-1", "defer");
        var labeller = Guid.CreateVersion7();

        var result = await CreateService().FlagMissedAsync(agentId, gcId, seq, labeller, Ct);

        Assert.True(result.Recorded);
        var label = Assert.Single(await new GateLabelRepository(db.DataSource).ListForDecisionAsync(decisionId, Ct));
        Assert.Equal("missed", label.Label);
        Assert.Equal("agent_flag", label.Source);
        Assert.Equal(labeller, label.Labeller);
    }

    [Fact]
    public async Task Flag_false_wake_writes_a_false_wake_label_with_agent_flag_source()
    {
        Assert.SkipWhen(PgSource.IsUnsupportedHost, "Windows without BAZAAR_TEST_PG: no test PostgreSQL available.");
        var agentId = await db.CreateAgentAsync();
        var (gcId, seq) = await db.CreateGcEventAsync();
        var decisionId = await InsertDecisionAsync(agentId, gcId, seq, "rules-1", "wake");
        var labeller = Guid.CreateVersion7();

        var result = await CreateService().FlagFalseWakeAsync(agentId, gcId, seq, labeller, Ct);

        Assert.True(result.Recorded);
        var label = Assert.Single(await new GateLabelRepository(db.DataSource).ListForDecisionAsync(decisionId, Ct));
        Assert.Equal("false_wake", label.Label);
        Assert.Equal("agent_flag", label.Source);
        Assert.Equal(labeller, label.Labeller);
    }

    [Fact]
    public async Task Flag_for_a_decision_that_does_not_exist_records_nothing()
    {
        Assert.SkipWhen(PgSource.IsUnsupportedHost, "Windows without BAZAAR_TEST_PG: no test PostgreSQL available.");
        var agentId = await db.CreateAgentAsync();
        var (gcId, seq) = await db.CreateGcEventAsync();
        var labeller = Guid.CreateVersion7();
        var service = CreateService();

        var missed = await service.FlagMissedAsync(agentId, gcId, seq, labeller, Ct);
        var falseWake = await service.FlagFalseWakeAsync(agentId, gcId, seq, labeller, Ct);

        Assert.False(missed.Recorded);
        Assert.Equal("not found", missed.Reason);
        Assert.False(falseWake.Recorded);
        Assert.Equal(0, await CountLabelsByLabellerAsync(labeller));
    }

    [Fact]
    public async Task Flag_for_another_agents_decision_records_nothing()
    {
        Assert.SkipWhen(PgSource.IsUnsupportedHost, "Windows without BAZAAR_TEST_PG: no test PostgreSQL available.");
        var owner = await db.CreateAgentAsync();
        var other = await db.CreateAgentAsync();
        var (gcId, seq) = await db.CreateGcEventAsync();
        await InsertDecisionAsync(owner, gcId, seq, "rules-1", "defer");
        var labeller = Guid.CreateVersion7();

        var result = await CreateService().FlagMissedAsync(other, gcId, seq, labeller, Ct);

        Assert.False(result.Recorded);
        Assert.Equal(0, await CountLabelsByLabellerAsync(labeller));
    }

    [Fact]
    public async Task Flag_labels_the_most_recent_decision_when_several_prompt_versions_exist()
    {
        Assert.SkipWhen(PgSource.IsUnsupportedHost, "Windows without BAZAAR_TEST_PG: no test PostgreSQL available.");
        var agentId = await db.CreateAgentAsync();
        var (gcId, seq) = await db.CreateGcEventAsync();
        var older = await InsertDecisionAsync(agentId, gcId, seq, "rules-1", "defer");
        await Task.Delay(20, Ct);
        var newer = await InsertDecisionAsync(agentId, gcId, seq, "rules-2", "defer");

        var result = await CreateService().FlagMissedAsync(agentId, gcId, seq, Guid.CreateVersion7(), Ct);

        Assert.True(result.Recorded);
        var labels = new GateLabelRepository(db.DataSource);
        Assert.Empty(await labels.ListForDecisionAsync(older, Ct));
        Assert.Single(await labels.ListForDecisionAsync(newer, Ct));
    }

    [Fact]
    public async Task Flag_does_not_change_gate_config_or_settings()
    {
        Assert.SkipWhen(PgSource.IsUnsupportedHost, "Windows without BAZAAR_TEST_PG: no test PostgreSQL available.");
        var agentId = await db.CreateAgentAsync();
        var (gcId, seq) = await db.CreateGcEventAsync();
        await InsertDecisionAsync(agentId, gcId, seq, "rules-1", "defer");
        var config = new GateConfigRepository(db.DataSource);
        var before = new GateConfig(agentId, """["gc:alpha"]""");
        await config.UpsertAsync(before, ct: Ct);
        var settingsBefore = await CountSettingsAsync();

        var service = CreateService();
        await service.FlagMissedAsync(agentId, gcId, seq, Guid.CreateVersion7(), Ct);
        await service.FlagFalseWakeAsync(agentId, gcId, seq, Guid.CreateVersion7(), Ct);

        Assert.Equal(before, await config.GetAsync(agentId, ct: Ct));
        Assert.Equal(settingsBefore, await CountSettingsAsync());
    }

    [Fact]
    public async Task Invalid_arguments_are_rejected_before_any_query()
    {
        var service = new GateFlagService(
            new GateLabelRepository(NpgsqlDataSource.Create("Host=127.0.0.1;Port=1;Username=unused")),
            new WakeDecisionRepository(NpgsqlDataSource.Create("Host=127.0.0.1;Port=1;Username=unused")));
        var agent = Guid.CreateVersion7();
        var gc = Guid.CreateVersion7();
        var labeller = Guid.CreateVersion7();

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => service.FlagMissedAsync(agent, gc, 0, labeller, Ct));
        await Assert.ThrowsAsync<ArgumentException>(() => service.FlagMissedAsync(Guid.Empty, gc, 1, labeller, Ct));
        await Assert.ThrowsAsync<ArgumentException>(() => service.FlagFalseWakeAsync(agent, Guid.Empty, 1, labeller, Ct));
        await Assert.ThrowsAsync<ArgumentException>(() => service.FlagFalseWakeAsync(agent, gc, 1, Guid.Empty, Ct));
    }

    [Fact]
    public void Flag_labels_are_the_closed_missed_and_false_wake_set()
    {
        Assert.True(GateLabelValues.FlagLabels.SetEquals(_expectedFlagLabels));
        Assert.Equal("agent_flag", GateLabelValues.SourceAgentFlag);
    }

    private async Task<Guid> InsertDecisionAsync(Guid agentId, Guid gcId, long seq, string promptVersion, string decision)
    {
        var id = Guid.CreateVersion7();
        var repo = new WakeDecisionRepository(db.DataSource);
        Assert.True(await repo.InsertAsync(new NewWakeDecision(id, gcId, seq, agentId, "default", decision, Shadow: true,
            ScoreBp: null, Category: null, ModelId: null, PromptVersion: promptVersion), ct: Ct));
        return id;
    }

    private async Task<long> CountLabelsByLabellerAsync(Guid labeller)
    {
        await using var cmd = db.DataSource.CreateCommand("SELECT count(*) FROM gate_labels WHERE labeller = $1");
        cmd.Parameters.AddWithValue(labeller);
        return (long)(await cmd.ExecuteScalarAsync(Ct))!;
    }

    private async Task<long> CountSettingsAsync()
    {
        await using var cmd = db.DataSource.CreateCommand("SELECT count(*) FROM setting");
        return (long)(await cmd.ExecuteScalarAsync(Ct))!;
    }
}
