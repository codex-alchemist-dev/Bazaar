using Bazaar.Server.Skimmer.Scoring;

namespace Bazaar.Server.Tests.Skimmer.Scoring;

public class FakeScorerTests
{
    private static readonly Guid _agent = Guid.Parse("0196a6f0-0000-7000-8000-000000000401");

    private static ScoreRequest Request(string text) =>
        new(new GcMessageRef(Guid.Empty, 1, text, "member_human"), [new AgentProfile(_agent, "Ada", [])]);

    [Fact]
    public async Task Returns_scripted_results_in_order_and_records_requests()
    {
        var first = new RelevanceResult(_agent, Category.Question, 9000);
        var second = new RelevanceResult(_agent, Category.Chat, 100);
        var scorer = new FakeScorer([FakeScorerStep.Returns([first]), FakeScorerStep.Returns([second])]);
        var a = Request("one");
        var b = Request("two");

        var r1 = await scorer.ScoreAsync(a, CancellationToken.None);
        var r2 = await scorer.ScoreAsync(b, CancellationToken.None);

        Assert.Equal([first], r1);
        Assert.Equal([second], r2);
        Assert.Equal(2, scorer.Requests.Count);
        Assert.Same(a, scorer.Requests[0]);
        Assert.Same(b, scorer.Requests[1]);
    }

    [Fact]
    public async Task Throws_the_scripted_exception()
    {
        var scorer = new FakeScorer([FakeScorerStep.Throws(new InvalidOperationException("gateway down"))]);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => scorer.ScoreAsync(Request("x"), CancellationToken.None));

        Assert.Equal("gateway down", ex.Message);
        Assert.Single(scorer.Requests);
    }

    [Fact]
    public async Task Timeout_step_throws_operation_canceled()
    {
        var scorer = new FakeScorer([FakeScorerStep.Timeout()]);

        await Assert.ThrowsAsync<OperationCanceledException>(() => scorer.ScoreAsync(Request("x"), CancellationToken.None));
        Assert.Single(scorer.Requests);
    }

    [Fact]
    public async Task Fails_when_the_script_is_exhausted()
    {
        var scorer = new FakeScorer([]);

        await Assert.ThrowsAsync<InvalidOperationException>(() => scorer.ScoreAsync(Request("x"), CancellationToken.None));
    }

    [Fact]
    public void Requests_list_is_read_only()
    {
        var scorer = new FakeScorer([]);

        Assert.IsAssignableFrom<IReadOnlyList<ScoreRequest>>(scorer.Requests);
        Assert.False(scorer.Requests is List<ScoreRequest>);
    }
}
