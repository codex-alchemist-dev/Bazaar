using Bazaar.Server.OutcomeLedger;
using Bazaar.Server.OutcomeLedger.Abstractions;
using Microsoft.Extensions.Time.Testing;

namespace Bazaar.Server.Reputation.Tests;

public class ScoreProjectionTests(LedgerDb db) : IClassFixture<LedgerDb>
{
    private static readonly DateTimeOffset _now = new(2026, 11, 22, 12, 0, 0, TimeSpan.Zero);

    private ScoreProjection Projection() =>
        new(new PgEvidenceSource(db.DataSource), WeightPolicy.Default, new FakeTimeProvider(_now));

    private async Task RecordAsync(string agent, string tag, OutcomeKind kind, decimal x, string key)
    {
        var recorder = new OutcomeRecorder(new PgOutcomeLedgerStore(db.DataSource), new OutcomeWeights(), TimeProvider.System, []);
        await recorder.RecordAsync(
            new OutcomeRecord(kind, "org/r#1", x, key, _now, [new OutcomeAttribution(agent, "author", tag)]),
            TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task No_evidence_is_the_prior()
    {
        var view = await Projection().GetAsync("nobody-" + Guid.NewGuid(), "csharp", TestContext.Current.CancellationToken);
        Assert.Equal(0.5, view.Score.S, 6);
        Assert.Equal(0, view.EvidenceCount);
    }

    [Fact]
    public async Task One_revert_gives_the_doc_example_score()
    {
        var agent = "a-" + Guid.NewGuid();
        await RecordAsync(agent, "doc11", OutcomeKind.Revert, 0m, "t:" + agent);

        var view = await Projection().GetAsync(agent, "doc11", TestContext.Current.CancellationToken);

        Assert.Equal(0.375, view.Score.S, 6);
        Assert.Equal(1, view.EvidenceCount);
    }

    [Fact]
    public async Task Only_the_newest_review_of_a_chain_counts()
    {
        var agent = "rv-" + Guid.NewGuid();
        var first = Guid.NewGuid();
        await using var cmd = db.DataSource.CreateCommand("""
            INSERT INTO review (id, pr_ref, reviewer_agent_id, author_agent_id, tag, overall, supersedes_id) VALUES
              (@first, 'org/r#9', 'r1', @agent, 'rev', 1, NULL),
              (gen_random_uuid(), 'org/r#9', 'r1', @agent, 'rev', 5, @first)
            """);
        cmd.Parameters.AddWithValue("first", first);
        cmd.Parameters.AddWithValue("agent", agent);
        await cmd.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);

        var view = await Projection().GetAsync(agent, "rev", TestContext.Current.CancellationToken);

        Assert.Equal(1, view.EvidenceCount);
        Assert.True(view.Score.S > 0.5);
    }

    [Fact]
    public async Task Suggestions_rank_by_the_lower_bound_and_respect_the_limit()
    {
        var good = "good-" + Guid.NewGuid();
        var bad = "bad-" + Guid.NewGuid();
        for (var i = 0; i < 3; i++)
        {
            await RecordAsync(good, "rank", OutcomeKind.OwnerAccept, 1m, $"g{i}:{good}");
            await RecordAsync(bad, "rank", OutcomeKind.OwnerReject, 0m, $"b{i}:{bad}");
        }

        var all = await Projection().SuggestAsync("rank", 10, TestContext.Current.CancellationToken);
        var one = await Projection().SuggestAsync("rank", 1, TestContext.Current.CancellationToken);

        Assert.Equal([good, bad], all.Select(s => s.AgentId).ToArray());
        Assert.Equal(good, Assert.Single(one).AgentId);
    }
}
