using Bazaar.Server.Agents.Turns;
using Bazaar.Server.Presence.Sources.Abstractions;
using Microsoft.Extensions.Time.Testing;

namespace Bazaar.Server.Tests.Agents.Turns;

/// <summary>The reporter that carries turn lifecycle events to the presence port (doc 06, docs/design/23). No database needed.</summary>
public sealed class TurnActivityReporterTests
{
    private static readonly Guid _agent = Guid.Parse("0196a6f0-0000-7000-8000-0000000000a1");
    private static readonly Guid _turn = Guid.Parse("0196a6f0-0000-7000-8000-0000000000b2");
    private static readonly Guid _gc = Guid.Parse("0196a6f0-0000-7000-8000-0000000000c3");

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    internal sealed class FakeSink(Func<TurnActivity, Exception?>? fail = null) : ITurnActivitySink
    {
        public List<TurnActivity> Activities { get; } = [];

        public ValueTask OnTurnActivityAsync(TurnActivity activity, CancellationToken ct)
        {
            Activities.Add(activity);
            return fail?.Invoke(activity) is { } ex ? throw ex : ValueTask.CompletedTask;
        }
    }

    [Fact]
    public async Task An_event_carries_the_agent_turn_gc_tool_and_the_clock_time()
    {
        var sink = new FakeSink();
        var clock = new FakeTimeProvider(new DateTimeOffset(2026, 11, 22, 9, 0, 0, TimeSpan.Zero));
        var reporter = new TurnActivityReporter(sink, clock);

        await reporter.ReportAsync(_agent, _turn, _gc.ToString("D"), TurnActivityKind.ToolStarted, "read_file", Ct);

        var activity = Assert.Single(sink.Activities);
        Assert.Equal(new TurnActivity(_agent, clock.GetUtcNow(), TurnActivityKind.ToolStarted, _turn, _gc, ToolName: "read_file"), activity);
    }

    [Fact]
    public async Task A_trigger_gc_that_is_not_a_guid_is_left_out()
    {
        var sink = new FakeSink();

        await new TurnActivityReporter(sink).ReportAsync(_agent, _turn, "gc-test-1", TurnActivityKind.TurnStarted, ct: Ct);

        Assert.Null(Assert.Single(sink.Activities).GcId);
    }

    [Fact]
    public async Task With_no_sink_every_call_is_a_no_op()
    {
        await new TurnActivityReporter(null).ReportAsync(_agent, _turn, null, TurnActivityKind.TurnEnded, ct: Ct);
    }

    [Fact]
    public async Task A_throwing_sink_does_not_fail_the_caller()
    {
        var sink = new FakeSink(_ => new InvalidOperationException("sink down"));

        await new TurnActivityReporter(sink).ReportAsync(_agent, _turn, null, TurnActivityKind.ModelCallStarted, ct: Ct);

        Assert.Single(sink.Activities);
    }

    [Fact]
    public async Task A_cancel_from_the_caller_still_reaches_the_caller()
    {
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();
        var sink = new FakeSink(_ => new OperationCanceledException(cts.Token));

        await Assert.ThrowsAsync<OperationCanceledException>(async () =>
            await new TurnActivityReporter(sink).ReportAsync(_agent, _turn, null, TurnActivityKind.TurnStarted, ct: cts.Token));
    }
}
