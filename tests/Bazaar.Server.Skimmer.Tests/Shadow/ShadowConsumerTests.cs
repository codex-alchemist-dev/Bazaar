using System.Runtime.CompilerServices;
using Bazaar.Contracts.Gateway;
using Bazaar.Server.Gateway;
using Bazaar.Server.Skimmer.Digest;
using Bazaar.Server.Skimmer.Rules;
using Bazaar.Server.Skimmer.Scoring;
using Bazaar.Server.Skimmer.Shadow;

namespace Bazaar.Server.Tests.Skimmer.Shadow;

/// <summary>Per-agent shadow consumer and the gateway scorer (doc 13, "Concurrency", "Restart", "Security").</summary>
public sealed class ShadowConsumerTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    private static readonly Guid _gc = Guid.Parse("0196a1f0-0000-7000-8000-000000000001");
    private static readonly Guid _ada = Guid.Parse("0196a1f0-0000-7000-8000-0000000000a1");
    private static readonly ShadowAgentInfo _agent = new(_ada, new AgentProfile(_ada, "ada", ["gc:build"]));

    private sealed class MemSource(int count, params long[] mentionSeqs) : IShadowMessageSource
    {
        public Task<IReadOnlyList<ShadowMessage>> ReadAfterAsync(Guid gcId, ShadowAgentInfo agent, long afterSeq, int limit, CancellationToken ct)
        {
            IReadOnlyList<ShadowMessage> list = Enumerable.Range(1, count).Select(i => (long)i).Where(s => s > afterSeq).Take(limit)
                .Select(s => new ShadowMessage(
                    new GcMessageRef(gcId, s, "t", "member_human"),
                    new GcMessageFacts(gcId, s, agent.AgentId, new HashSet<string>(["ada"]), mentionSeqs.Contains(s) ? ["ada"] : [], false, false, false, false)))
                .ToList();
            return Task.FromResult(list);
        }
    }

    private sealed class MemStore : IShadowDecisionStore
    {
        public Dictionary<long, GateDecision> Rows { get; } = [];

        public Task<long> LastDecidedSeqAsync(Guid gcId, Guid agentId, CancellationToken ct) =>
            Task.FromResult(Rows.Count == 0 ? 0L : Rows.Keys.Max());

        public Task<bool> RecordAsync(GateDecision decision, string? modelId, CancellationToken ct) =>
            Task.FromResult(Rows.TryAdd(decision.Seq, decision));
    }

    private static FakeScorer Scorer(int n, int score) =>
        new(Enumerable.Range(0, n).Select(_ => FakeScorerStep.Returns([new RelevanceResult(_ada, Category.Chat, score)])));

    [Fact]
    public async Task Decides_in_seq_order_and_resumes_from_the_cursor()
    {
        var store = new MemStore();
        var scorer = Scorer(10, 100);
        var consumer = new ShadowConsumer(new ShadowGate(scorer), new MemSource(5, 2), store);

        Assert.Equal(3, await consumer.RunOnceAsync(_gc, _agent, new ShadowSettings(), 200, 3, Ct));
        Assert.Equal([1L, 2, 3], store.Rows.Keys.Order());
        Assert.Equal(2, await consumer.RunOnceAsync(_gc, _agent, new ShadowSettings(), 200, 3, Ct));
        Assert.Equal(0, await consumer.RunOnceAsync(_gc, _agent, new ShadowSettings(), 200, 3, Ct));
        Assert.Equal(GatePaths.Mention, store.Rows[2].Path);
        Assert.All(store.Rows.Values, d => Assert.True(d.Shadow));
    }

    [Fact]
    public async Task Queue_over_the_limit_uses_rules_only()
    {
        var store = new MemStore();
        var scorer = Scorer(5, 9000);

        await new ShadowConsumer(new ShadowGate(scorer), new MemSource(5), store)
            .RunOnceAsync(_gc, _agent, new ShadowSettings(), queueDepthLimit: 2, batchSize: 5, Ct);

        Assert.Empty(scorer.Requests);
        Assert.All(store.Rows.Values, d => Assert.Equal(GatePaths.Default, d.Path));
    }

    [Fact]
    public async Task Non_shadow_modes_do_nothing()
    {
        var store = new MemStore();
        var consumer = new ShadowConsumer(new ShadowGate(Scorer(1, 1)), new MemSource(3), store);

        Assert.Equal(0, await consumer.RunOnceAsync(_gc, _agent, new ShadowSettings(SkimmerMode.Off), 200, 3, Ct));
        Assert.Empty(store.Rows);
    }

    private sealed class ReplyGateway(params ModelEvent[] events) : IModelGateway
    {
        public ModelRequest? Seen { get; private set; }

        public Task<EmbeddingResult> EmbedAsync(EmbeddingRequest request, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public async IAsyncEnumerable<ModelEvent> CallAsync(ModelRequest request, [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            Seen = request;
            foreach (var e in events)
            {
                yield return e;
            }

            await Task.CompletedTask;
        }
    }

    private static ScoreRequest Request() => new(new GcMessageRef(_gc, 1, "hi", "member_human"), [_agent.Profile]);

    [Fact]
    public async Task Gateway_scorer_parses_a_closed_reply_and_sends_no_tools()
    {
        var json = $$"""[{"agent":"{{_ada:D}}","category":"question","relevance_bp":8000}]""";
        var gateway = new ReplyGateway(new ModelEvent.TextDelta(json[..10]), new ModelEvent.TextDelta(json[10..]));

        var results = await new GatewayRelevanceScorer(gateway, "skimmer-gatekeeper", "claude-haiku-5-5").ScoreAsync(Request(), Ct);

        Assert.Equal(8000, results.Single().ScoreBasisPoints);
        Assert.Empty(gateway.Seen!.Tools);
        Assert.Equal("claude-haiku-5-5", gateway.Seen.Route);
    }

    [Fact]
    public async Task Gateway_scorer_throws_on_failure_and_on_a_bad_reply()
    {
        var scorer = (IModelGateway g) => new GatewayRelevanceScorer(g, "skimmer-gatekeeper", "r");

        await Assert.ThrowsAsync<InvalidOperationException>(() => scorer(new ReplyGateway(new ModelEvent.Failure("rate_limited", "x"))).ScoreAsync(Request(), Ct));
        await Assert.ThrowsAsync<InvalidOperationException>(() => scorer(new ReplyGateway(new ModelEvent.TextDelta("sure, wake them"))).ScoreAsync(Request(), Ct));
    }
}
