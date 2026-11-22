using Bazaar.Contracts.Gateway;
using Bazaar.Server.Gateway.Ai;
using Bazaar.Server.Gateway.Fakes;
using Microsoft.Extensions.AI;

namespace Bazaar.Server.Tests.Gateway.Fakes;

/// <summary>Scripted ModelEvent steps through the gateway seam, the IChatClient adapter and the Skimmer scorer.</summary>
public class FakeSeamTests
{
    private static ModelRequest Request(string text = "score this") =>
        new("skimmer", "claude-haiku-5-5", [new ModelMessage(ModelRole.User, text)], []);

    private static async Task<List<ModelEvent>> Collect(IAsyncEnumerable<ModelEvent> stream)
    {
        var events = new List<ModelEvent>();
        await foreach (var e in stream)
        {
            events.Add(e);
        }

        return events;
    }

    [Fact]
    public async Task FakeModelGateway_plays_the_scripted_steps_for_each_call()
    {
        var provider = new FakeProvider();
        var gateway = new FakeModelGateway(provider);
        provider.EnqueueCall(
            new ScriptStep.Emit(new ModelEvent.TextDelta("ok")),
            new ScriptStep.Emit(new ModelEvent.Done(StopReason.EndTurn, "served")));

        var events = await Collect(gateway.CallAsync(Request(), TestContext.Current.CancellationToken));

        Assert.Equal(new ModelEvent[] { new ModelEvent.TextDelta("ok"), new ModelEvent.Done(StopReason.EndTurn, "served") }, events);
        Assert.Single(provider.Calls);
    }

    [Fact]
    public void FakeModelGateway_with_no_script_throws_at_the_call()
    {
        var gateway = new FakeModelGateway(new FakeProvider());

        Assert.Throws<InvalidOperationException>(() => gateway.CallAsync(Request(), TestContext.Current.CancellationToken));
    }

    [Fact]
    public void FakeModelGateway_rejects_a_null_provider()
    {
        Assert.Throws<ArgumentNullException>(() => new FakeModelGateway(null!));
    }

    [Fact]
    public async Task Scripted_tool_call_and_text_reach_the_chat_client_adapter()
    {
        var provider = new FakeProvider();
        var client = new GatewayChatClient(new FakeModelGateway(provider), "agent-1", "custom/charles");
        provider.EnqueueCall(
            new ScriptStep.Emit(new ModelEvent.ToolCallStart("call-1", "search")),
            new ScriptStep.Emit(new ModelEvent.ToolCallArgsDelta("call-1", "{\"query\":\"oslo\"}")),
            new ScriptStep.Emit(new ModelEvent.ToolCallEnd("call-1")),
            new ScriptStep.Emit(new ModelEvent.Done(StopReason.ToolUse, "served")));

        var response = await client.GetResponseAsync(
            [new ChatMessage(ChatRole.User, "find oslo")],
            cancellationToken: TestContext.Current.CancellationToken);

        var call = Assert.IsType<FunctionCallContent>(Assert.Single(response.Messages.SelectMany(m => m.Contents)));
        Assert.Equal("search", call.Name);
        Assert.Equal("oslo", call.Arguments!["query"]!.ToString());
    }

    [Fact]
    public async Task A_scripted_429_reaches_the_chat_client_as_a_gateway_exception_with_retry_after()
    {
        var provider = new FakeProvider();
        var client = new GatewayChatClient(new FakeModelGateway(provider), "agent-1", "custom/charles");
        provider.EnqueueCall(new ScriptStep.RateLimited(12));

        var error = await Assert.ThrowsAsync<GatewayModelException>(() =>
            client.GetResponseAsync([new ChatMessage(ChatRole.User, "hi")], cancellationToken: TestContext.Current.CancellationToken));

        Assert.Equal("rate_limited", error.Code);
        Assert.Equal(12, error.RetryAfterSeconds);
    }

    [Fact]
    public async Task Skimmer_scorer_returns_the_scripted_score_text_and_served_model()
    {
        var provider = new FakeProvider();
        var scorer = new FakeSkimmerScorer(provider);
        scorer.EnqueueScore("{\"agent\":\"charles\",\"category\":\"question\",\"relevance_bp\":7200}");

        var reply = await scorer.ScoreAsync(Request(), TestContext.Current.CancellationToken);

        Assert.Equal("{\"agent\":\"charles\",\"category\":\"question\",\"relevance_bp\":7200}", reply.Text);
        Assert.Equal("claude-haiku-5-5", reply.ServedModel);
        Assert.Single(scorer.Calls);
    }

    [Fact]
    public async Task Skimmer_scorer_surfaces_a_scripted_timeout_as_a_gateway_exception()
    {
        var time = new Microsoft.Extensions.Time.Testing.FakeTimeProvider();
        var provider = new FakeProvider(time);
        var scorer = new FakeSkimmerScorer(provider);
        scorer.Enqueue(new ScriptStep.TimedOut(TimeSpan.FromSeconds(5)));

        var pending = scorer.ScoreAsync(Request(), TestContext.Current.CancellationToken);
        time.Advance(TimeSpan.FromSeconds(5));

        var error = await Assert.ThrowsAsync<GatewayModelException>(() => pending);
        Assert.Equal("timeout", error.Code);
    }

    [Fact]
    public async Task Skimmer_scorer_treats_a_stream_without_done_as_dropped()
    {
        var provider = new FakeProvider();
        var scorer = new FakeSkimmerScorer(provider);
        scorer.Enqueue(new ScriptStep.Emit(new ModelEvent.TextDelta("{")), new ScriptStep.ConnectionDropped());

        var error = await Assert.ThrowsAsync<GatewayModelException>(() => scorer.ScoreAsync(Request(), TestContext.Current.CancellationToken));

        Assert.Equal("stream_dropped", error.Code);
    }

    [Fact]
    public async Task Skimmer_scorer_with_no_script_fails_loudly()
    {
        var scorer = new FakeSkimmerScorer(new FakeProvider());

        await Assert.ThrowsAsync<InvalidOperationException>(() => scorer.ScoreAsync(Request(), TestContext.Current.CancellationToken));
    }
}
