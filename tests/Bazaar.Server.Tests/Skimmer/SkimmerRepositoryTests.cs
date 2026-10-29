using Bazaar.Server.Skimmer.Data;

namespace Bazaar.Server.Tests.Skimmer;

/// <summary>CRUD round trips for the Skimmer gate repositories on real PostgreSQL (doc 13, WP1).</summary>
public sealed class SkimmerRepositoryTests(SkimmerDb db) : IClassFixture<SkimmerDb>
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Gate_config_reads_null_then_round_trips_and_updates_subscriptions()
    {
        var repo = new GateConfigRepository(db.DataSource);
        var agentId = await db.CreateAgentAsync();

        Assert.Null(await repo.GetAsync(agentId, ct: Ct));

        await repo.UpsertAsync(new GateConfig(agentId, """["gc:alpha"]"""), ct: Ct);
        Assert.Equal(new GateConfig(agentId, """["gc:alpha"]"""), await repo.GetAsync(agentId, ct: Ct));

        await repo.UpsertAsync(new GateConfig(agentId, "[]"), ct: Ct);
        Assert.Equal(new GateConfig(agentId, "[]"), await repo.GetAsync(agentId, ct: Ct));
    }

    [Fact]
    public async Task Wake_decision_round_trips_with_a_score_and_a_model()
    {
        var repo = new WakeDecisionRepository(db.DataSource);
        var agentId = await db.CreateAgentAsync();
        var (gcId, seq) = await db.CreateGcEventAsync();
        var created = new NewWakeDecision(Guid.CreateVersion7(), gcId, seq, agentId, "scored", "defer", Shadow: true,
            ScoreBp: 7200, Category: "review", ModelId: "claude-haiku-5-5", PromptVersion: "score-1");

        Assert.True(await repo.InsertAsync(created, ct: Ct));

        var expected = new WakeDecisionRecord(created.Id, gcId, seq, agentId, "scored", "defer", true, 7200, "review", "claude-haiku-5-5", "score-1", default);
        var byId = await repo.GetAsync(created.Id, ct: Ct);
        Assert.NotNull(byId);
        AssertSameDecision(expected, byId);
        Assert.True(byId.CreatedAt > DateTimeOffset.UtcNow.AddMinutes(-5));

        var byKey = await repo.FindAsync(gcId, seq, agentId, "score-1", Ct);
        Assert.Equal(byId, byKey);
    }

    [Fact]
    public async Task Rules_decision_stores_null_score_category_and_model()
    {
        var repo = new WakeDecisionRepository(db.DataSource);
        var agentId = await db.CreateAgentAsync();
        var (gcId, seq) = await db.CreateGcEventAsync();
        var created = new NewWakeDecision(Guid.CreateVersion7(), gcId, seq, agentId, "dm", "wake", Shadow: false,
            ScoreBp: null, Category: null, ModelId: null, PromptVersion: "rules-1");

        Assert.True(await repo.InsertAsync(created, ct: Ct));

        var read = await repo.GetAsync(created.Id, ct: Ct);
        Assert.NotNull(read);
        Assert.Null(read.ScoreBp);
        Assert.Null(read.Category);
        Assert.Null(read.ModelId);
        Assert.False(read.Shadow);
        Assert.Equal("wake", read.Decision);
    }

    [Fact]
    public async Task Repeating_a_decision_key_writes_nothing_new()
    {
        var repo = new WakeDecisionRepository(db.DataSource);
        var agentId = await db.CreateAgentAsync();
        var (gcId, seq) = await db.CreateGcEventAsync();
        var first = new NewWakeDecision(Guid.CreateVersion7(), gcId, seq, agentId, "mention", "wake", true, null, null, null, "rules-1");
        var repeat = first with { Id = Guid.CreateVersion7(), Decision = "defer" };

        Assert.True(await repo.InsertAsync(first, ct: Ct));
        Assert.False(await repo.InsertAsync(repeat, ct: Ct));

        Assert.Null(await repo.GetAsync(repeat.Id, ct: Ct));
        var kept = await repo.FindAsync(gcId, seq, agentId, "rules-1", Ct);
        Assert.NotNull(kept);
        Assert.Equal(first.Id, kept.Id);
        Assert.Equal("wake", kept.Decision);
    }

    [Fact]
    public async Task Labels_attach_to_one_decision_and_list_back()
    {
        var decisions = new WakeDecisionRepository(db.DataSource);
        var labels = new GateLabelRepository(db.DataSource);
        var agentId = await db.CreateAgentAsync();
        var (gcId, seq) = await db.CreateGcEventAsync();
        var target = new NewWakeDecision(Guid.CreateVersion7(), gcId, seq, agentId, "default", "defer", true, null, null, null, "rules-1");
        var other = target with { Id = Guid.CreateVersion7(), PromptVersion = "rules-2" };
        Assert.True(await decisions.InsertAsync(target, ct: Ct));
        Assert.True(await decisions.InsertAsync(other, ct: Ct));

        var missed = new NewGateLabel(Guid.CreateVersion7(), target.Id, "should_wake", "agent_flag", agentId);
        var owner = new NewGateLabel(Guid.CreateVersion7(), target.Id, "should_wake", "owner_review", Guid.CreateVersion7());
        await labels.InsertAsync(missed, ct: Ct);
        await labels.InsertAsync(owner, ct: Ct);
        await labels.InsertAsync(new NewGateLabel(Guid.CreateVersion7(), other.Id, "should_defer", "outcome", Guid.CreateVersion7()), ct: Ct);

        var listed = await labels.ListForDecisionAsync(target.Id, Ct);
        Assert.Equal(2, listed.Count);
        Assert.All(listed, l => Assert.Equal(target.Id, l.DecisionId));
        var first = Assert.Single(listed, l => l.Id == missed.Id);
        Assert.Equal("should_wake", first.Label);
        Assert.Equal("agent_flag", first.Source);
        Assert.Equal(agentId, first.Labeller);
        Assert.Contains(listed, l => l.Id == owner.Id && l.Source == "owner_review");
    }

    private static void AssertSameDecision(WakeDecisionRecord expected, WakeDecisionRecord actual)
    {
        Assert.Equal(expected.Id, actual.Id);
        Assert.Equal(expected.GcId, actual.GcId);
        Assert.Equal(expected.Seq, actual.Seq);
        Assert.Equal(expected.AgentId, actual.AgentId);
        Assert.Equal(expected.Path, actual.Path);
        Assert.Equal(expected.Decision, actual.Decision);
        Assert.Equal(expected.Shadow, actual.Shadow);
        Assert.Equal(expected.ScoreBp, actual.ScoreBp);
        Assert.Equal(expected.Category, actual.Category);
        Assert.Equal(expected.ModelId, actual.ModelId);
        Assert.Equal(expected.PromptVersion, actual.PromptVersion);
    }
}
