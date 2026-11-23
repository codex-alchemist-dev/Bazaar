using Bazaar.Contracts.Gateway;
using Bazaar.Server.Gateway.Fakes;
using Microsoft.Extensions.Time.Testing;

namespace Bazaar.Server.Tests.Gateway.Fakes;

public class FakeProviderTests
{
    private static ModelRequest Request(string agentId = "agent-1") =>
        new(agentId, "route-a", [new ModelMessage(ModelRole.User, "hello")], []);

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
    public async Task Emit_yields_the_event_unchanged()
    {
        var provider = new FakeProvider();
        provider.EnqueueCall(
            new ScriptStep.Emit(new ModelEvent.TextDelta("Hi")),
            new ScriptStep.Emit(new ModelEvent.Done(StopReason.EndTurn, "served-model")));

        var events = await Collect(provider.StreamAsync(Request(), TestContext.Current.CancellationToken));

        var expected = new ModelEvent[] { new ModelEvent.TextDelta("Hi"), new ModelEvent.Done(StopReason.EndTurn, "served-model") };
        Assert.Equal(expected, events);
    }

    [Fact]
    public async Task Emit_can_script_a_wrong_served_model()
    {
        var provider = new FakeProvider();
        provider.EnqueueCall(new ScriptStep.Emit(new ModelEvent.Done(StopReason.EndTurn, "other-model")));

        var events = await Collect(provider.StreamAsync(Request(), TestContext.Current.CancellationToken));

        var done = Assert.IsType<ModelEvent.Done>(Assert.Single(events));
        Assert.Equal("other-model", done.ServedModel);
    }

    [Fact]
    public async Task RateLimited_yields_rate_limited_with_retry_after_and_ends_the_stream()
    {
        var provider = new FakeProvider();
        provider.EnqueueCall(new ScriptStep.RateLimited(RetryAfterSeconds: 30));

        var failure = Assert.IsType<ModelEvent.Failure>(Assert.Single(await Collect(provider.StreamAsync(Request(), TestContext.Current.CancellationToken))));

        Assert.Equal("rate_limited", failure.Code);
        Assert.Equal(30, failure.RetryAfterSeconds);
    }

    [Fact]
    public async Task RateLimited_without_retry_after_leaves_it_unset()
    {
        var provider = new FakeProvider();
        provider.EnqueueCall(new ScriptStep.RateLimited());

        var failure = Assert.IsType<ModelEvent.Failure>(Assert.Single(await Collect(provider.StreamAsync(Request(), TestContext.Current.CancellationToken))));

        Assert.Equal("rate_limited", failure.Code);
        Assert.Null(failure.RetryAfterSeconds);
    }

    [Fact]
    public async Task SpendCapped_yields_spend_capped_without_retry_after()
    {
        var provider = new FakeProvider();
        provider.EnqueueCall(new ScriptStep.SpendCapped());

        var failure = Assert.IsType<ModelEvent.Failure>(Assert.Single(await Collect(provider.StreamAsync(Request(), TestContext.Current.CancellationToken))));

        Assert.Equal("spend_capped", failure.Code);
        Assert.Null(failure.RetryAfterSeconds);
    }

    [Fact]
    public async Task TimedOut_waits_on_the_provider_clock_then_yields_timeout()
    {
        var time = new FakeTimeProvider();
        var provider = new FakeProvider(time);
        provider.EnqueueCall(new ScriptStep.TimedOut(TimeSpan.FromSeconds(30)));

        var pending = Collect(provider.StreamAsync(Request(), TestContext.Current.CancellationToken));
        time.Advance(TimeSpan.FromSeconds(29));
        Assert.False(pending.IsCompleted);

        time.Advance(TimeSpan.FromSeconds(1));
        var failure = Assert.IsType<ModelEvent.Failure>(Assert.Single(await pending));
        Assert.Equal("timeout", failure.Code);
    }

    [Fact]
    public async Task TimedOut_honours_cancellation_while_waiting()
    {
        var provider = new FakeProvider(new FakeTimeProvider());
        provider.EnqueueCall(new ScriptStep.TimedOut(TimeSpan.FromMinutes(5)));
        using var cts = new CancellationTokenSource();

        var pending = Collect(provider.StreamAsync(Request(), cts.Token));
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
    }

    [Fact]
    public async Task MalformedToolJson_streams_the_fragment_then_fails_with_malformed_tool_json()
    {
        var provider = new FakeProvider();
        provider.EnqueueCall(new ScriptStep.MalformedToolJson("call-1", "search", "{\"query\": \"oslo"));

        var events = await Collect(provider.StreamAsync(Request(), TestContext.Current.CancellationToken));

        Assert.Equal(4, events.Count);
        Assert.Equal(new ModelEvent.ToolCallStart("call-1", "search"), events[0]);
        Assert.Equal(new ModelEvent.ToolCallArgsDelta("call-1", "{\"query\": \"oslo"), events[1]);
        Assert.Equal(new ModelEvent.ToolCallEnd("call-1"), events[2]);
        var failure = Assert.IsType<ModelEvent.Failure>(events[3]);
        Assert.Equal("malformed_tool_json", failure.Code);
    }

    [Theory]
    [InlineData("{\"query\": ")]
    [InlineData("not json at all")]
    [InlineData("")]
    public void MalformedToolJson_accepts_arguments_that_are_not_valid_json(string raw)
    {
        var provider = new FakeProvider();

        provider.EnqueueCall(new ScriptStep.MalformedToolJson("call-1", "search", raw));

        Assert.Equal(1, provider.RemainingScripts);
    }

    [Fact]
    public void MalformedToolJson_rejects_arguments_that_are_valid_json()
    {
        var provider = new FakeProvider();

        Assert.Throws<ArgumentException>(() =>
            provider.EnqueueCall(new ScriptStep.MalformedToolJson("call-1", "search", "{\"query\": \"oslo\"}")));
        Assert.Equal(0, provider.RemainingScripts);
    }

    [Fact]
    public async Task ConnectionDropped_keeps_partial_output_and_yields_stream_dropped_without_done()
    {
        var provider = new FakeProvider();
        provider.EnqueueCall(
            new ScriptStep.Emit(new ModelEvent.TextDelta("Hel")),
            new ScriptStep.ConnectionDropped());

        var events = await Collect(provider.StreamAsync(Request(), TestContext.Current.CancellationToken));

        Assert.Equal(2, events.Count);
        Assert.Equal(new ModelEvent.TextDelta("Hel"), events[0]);
        var failure = Assert.IsType<ModelEvent.Failure>(events[1]);
        Assert.Equal("stream_dropped", failure.Code);
        Assert.DoesNotContain(events, e => e is ModelEvent.Done);
    }

    [Theory]
    [MemberData(nameof(TerminalStepsNotLast))]
    public void A_terminal_step_must_be_the_last_step(ScriptStep terminal)
    {
        var provider = new FakeProvider();

        Assert.Throws<ArgumentException>(() =>
            provider.EnqueueCall(terminal, new ScriptStep.Emit(new ModelEvent.TextDelta("late"))));
        Assert.Equal(0, provider.RemainingScripts);
    }

    public static TheoryData<ScriptStep> TerminalStepsNotLast() =>
    [
        new ScriptStep.RateLimited(),
        new ScriptStep.SpendCapped(),
        new ScriptStep.TimedOut(TimeSpan.Zero),
        new ScriptStep.ConnectionDropped(),
        new ScriptStep.Emit(new ModelEvent.Failure("provider_error", "boom")),
        new ScriptStep.Emit(new ModelEvent.Done(StopReason.EndTurn, "m")),
    ];

    [Fact]
    public void An_empty_script_is_rejected()
    {
        var provider = new FakeProvider();

        Assert.Throws<ArgumentException>(() => provider.EnqueueCall());
    }

    [Fact]
    public async Task Each_call_plays_its_own_script_in_order_and_is_recorded()
    {
        var provider = new FakeProvider();
        provider.EnqueueCall(new ScriptStep.RateLimited(5));
        provider.EnqueueCall(new ScriptStep.Emit(new ModelEvent.TextDelta("second")));

        var first = await Collect(provider.StreamAsync(Request("agent-a"), TestContext.Current.CancellationToken));
        var second = await Collect(provider.StreamAsync(Request("agent-b"), TestContext.Current.CancellationToken));

        Assert.Equal(new ModelEvent.Failure("rate_limited", "The upstream returned 429.", 5), first[0]);
        Assert.Equal(new ModelEvent.TextDelta("second"), second[0]);
        Assert.Equal(new List<string> { "agent-a", "agent-b" }, provider.Calls.Select(c => c.AgentId));
        Assert.Equal(0, provider.RemainingScripts);
    }

    [Fact]
    public void A_call_with_no_script_fails_loudly_and_is_still_recorded()
    {
        var provider = new FakeProvider();

        Assert.Throws<InvalidOperationException>(() => provider.StreamAsync(Request("unexpected"), TestContext.Current.CancellationToken));
        Assert.Equal(new List<string> { "unexpected" }, provider.Calls.Select(c => c.AgentId));
    }
}
