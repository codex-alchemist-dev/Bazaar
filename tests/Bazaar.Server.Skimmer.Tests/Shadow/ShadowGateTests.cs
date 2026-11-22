using Bazaar.Server.Skimmer.Digest;
using Bazaar.Server.Skimmer.Rules;
using Bazaar.Server.Skimmer.Scoring;
using Bazaar.Server.Skimmer.Shadow;

namespace Bazaar.Server.Tests.Skimmer.Shadow;

/// <summary>Shadow-mode gate and metrics on a fixture (doc 13, "Modes", "Measurement", "Testing").</summary>
public sealed class ShadowGateTests
{
    private static readonly Guid _gc = Guid.Parse("0196a1f0-0000-7000-8000-000000000001");
    private static readonly Guid _ada = Guid.Parse("0196a1f0-0000-7000-8000-0000000000a1");
    private static readonly Guid _bo = Guid.Parse("0196a1f0-0000-7000-8000-0000000000b2");

    private static ShadowAgent Agent(Guid id, string handle, long seq, params string[] mentions) =>
        new(new GcMessageFacts(_gc, seq, id, new HashSet<string>([handle]), mentions, false, false, false, false),
            new AgentProfile(id, handle, ["gc:build"]));

    private static GcMessageRef Msg(long seq) => new(_gc, seq, "text", "member_human");

    [Fact]
    public async Task Mention_wakes_even_when_scorer_says_zero_and_nothing_is_promoted()
    {
        var scorer = new FakeScorer([FakeScorerStep.Returns([new RelevanceResult(_bo, Category.Chat, 0)])]);
        var gate = new ShadowGate(scorer);

        var result = await gate.DecideAsync(Msg(1), [Agent(_ada, "ada", 1, "ada"), Agent(_bo, "bo", 1)], new ShadowSettings(), TestContext.Current.CancellationToken);

        Assert.All(result, d => Assert.True(d.Shadow));
        Assert.Equal((GatePaths.Mention, GateDecisions.Wake), (result[0].Path, result[0].Decision));
        Assert.Equal((GatePaths.Default, GateDecisions.Defer), (result[1].Path, result[1].Decision));
        Assert.Single(scorer.Requests);
        Assert.Single(scorer.Requests[0].Agents);
    }

    [Theory]
    [InlineData(6999, GateDecisions.Defer, GatePaths.Default)]
    [InlineData(7000, GateDecisions.Wake, GatePaths.Scored)]
    public async Task Threshold_edge(int score, string decision, string path)
    {
        var gate = new ShadowGate(new FakeScorer([FakeScorerStep.Returns([new RelevanceResult(_ada, Category.Question, score)])]));

        var d = (await gate.DecideAsync(Msg(2), [Agent(_ada, "ada", 2)], new ShadowSettings(), TestContext.Current.CancellationToken)).Single();

        Assert.Equal((path, decision, score), (d.Path, d.Decision, d.ScoreBasisPoints));
        Assert.True(d.Shadow);
    }

    [Fact]
    public async Task Scorer_failure_and_timeout_fall_back_to_defer()
    {
        var gate = new ShadowGate(new FakeScorer([FakeScorerStep.Throws(new InvalidOperationException()), FakeScorerStep.Timeout()]));

        var a = (await gate.DecideAsync(Msg(3), [Agent(_ada, "ada", 3)], new ShadowSettings(), TestContext.Current.CancellationToken)).Single();
        var b = (await gate.DecideAsync(Msg(4), [Agent(_ada, "ada", 4)], new ShadowSettings(), TestContext.Current.CancellationToken)).Single();

        Assert.All([a, b], d => Assert.Equal((GatePaths.Default, GateDecisions.Defer, (int?)null), (d.Path, d.Decision, d.ScoreBasisPoints)));
    }

    [Fact]
    public async Task Caller_cancellation_propagates()
    {
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();
        var gate = new ShadowGate(new FakeScorer([FakeScorerStep.Timeout()]));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => gate.DecideAsync(Msg(5), [Agent(_ada, "ada", 5)], new ShadowSettings(), cts.Token));
    }

    [Fact]
    public async Task Off_records_nothing_and_defer_is_refused()
    {
        var scorer = new FakeScorer([]);
        var gate = new ShadowGate(scorer);

        Assert.Empty(await gate.DecideAsync(Msg(6), [Agent(_ada, "ada", 6)], new ShadowSettings(SkimmerMode.Off), TestContext.Current.CancellationToken));
        await Assert.ThrowsAsync<NotSupportedException>(() => gate.DecideAsync(Msg(6), [Agent(_ada, "ada", 6)], new ShadowSettings(SkimmerMode.Defer), TestContext.Current.CancellationToken));
        Assert.Empty(scorer.Requests);
    }

    [Fact]
    public async Task Fixture_precision_and_miss_rate()
    {
        // Ten unmentioned messages for one agent: score and the label of whether it should have woken.
        (int Score, bool Should)[] fixture =
            [(9000, true), (8500, true), (7500, true), (7200, false), (7000, false), (6500, true), (3000, false), (1000, false), (500, false), (200, false)];
        var scorer = new FakeScorer(fixture.Select(f => FakeScorerStep.Returns([new RelevanceResult(_ada, Category.Chat, f.Score)])));
        var gate = new ShadowGate(scorer);
        var labelled = new List<LabelledDecision>();
        for (var i = 0; i < fixture.Length; i++)
        {
            var d = (await gate.DecideAsync(Msg(i + 1), [Agent(_ada, "ada", i + 1)], new ShadowSettings(), TestContext.Current.CancellationToken)).Single();
            labelled.Add(new LabelledDecision(d, fixture[i].Should));
        }

        var m = ShadowMetrics.Compute(labelled, minLabels: 10, maxMissBoundBasisPoints: 5000);

        Assert.Equal(10, m.Labelled);
        Assert.Equal(5, m.WouldPromote);
        Assert.Equal(3, m.TruePromotions);
        Assert.Equal(2, m.FalsePromotions);
        Assert.Equal(6000, m.PrecisionBasisPoints);
        Assert.Equal(1, m.Misses);
        Assert.Equal(2500, m.MissRateBasisPoints);
        Assert.True(m.MissUpperBoundBasisPoints > m.MissRateBasisPoints);
        Assert.False(m.ReadyForPromotion);
    }

    [Fact]
    public void Empty_and_non_shadow_input_gives_null_rates_and_not_ready()
    {
        var live = new GateDecision(_ada, _gc, 1, GatePaths.Scored, GateDecisions.Wake, "chat", 9000, Shadow: false);

        var m = ShadowMetrics.Compute([new LabelledDecision(live, true)]);

        Assert.Equal(0, m.Labelled);
        Assert.Null(m.PrecisionBasisPoints);
        Assert.Null(m.MissRateBasisPoints);
        Assert.False(m.ReadyForPromotion);
    }

    [Fact]
    public void Ready_when_enough_labels_and_no_misses()
    {
        var rows = Enumerable.Range(1, 1000)
            .Select(i => new LabelledDecision(new GateDecision(_ada, _gc, i, GatePaths.Mention, GateDecisions.Wake, null, null, true), true));

        var m = ShadowMetrics.Compute(rows);

        Assert.Equal(0, m.MissRateBasisPoints);
        Assert.True(m.ReadyForPromotion);
    }
}
