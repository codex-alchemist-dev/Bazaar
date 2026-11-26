using Bazaar.Server.Presence.Sources.Abstractions;
using Bazaar.Server.WorkLife;
using Microsoft.Extensions.Time.Testing;

namespace Bazaar.Server.Presence.Sources.Tests;

/// <summary>The wrapper reports each applied submit to presence and never lets the sink fail the caller (doc 09, doc 23).</summary>
public class PresenceNotifyingAvailabilityMachineTests
{
    private static readonly Guid _agent = Guid.Parse("0196a6f0-0000-7000-8000-0000000000a1");
    private static readonly DateTimeOffset _t = new(2026, 11, 22, 9, 0, 0, TimeSpan.Zero);

    private sealed class FakeSink(bool fail = false) : IAvailabilityTransitionSink
    {
        public List<(Guid Agent, AvailabilityTransition Transition, DateTimeOffset At)> Calls { get; } = [];

        public ValueTask OnTransitionAsync(Guid agentId, AvailabilityTransition transition, DateTimeOffset at, CancellationToken ct)
        {
            Calls.Add((agentId, transition, at));
            return fail ? throw new InvalidOperationException("sink down") : ValueTask.CompletedTask;
        }
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static AvailabilityEvent Start => new AvailabilityEvent.ScheduleStart(_t);

    [Fact]
    public async Task An_applied_submit_is_reported_with_the_agent_and_the_clock_time()
    {
        var sink = new FakeSink();
        var clock = new FakeTimeProvider(_t.AddMinutes(1));
        var wrapped = new PresenceNotifyingAvailabilityMachine(new AvailabilityMachine("a1", AvailabilityMachineState.Off), _agent, sink, clock);

        var transition = await wrapped.SubmitAsync(Start, Ct);

        Assert.True(transition.Applied);
        var call = Assert.Single(sink.Calls);
        Assert.Equal(_agent, call.Agent);
        Assert.Equal(transition, call.Transition);
        Assert.Equal(_t.AddMinutes(1), call.At);
    }

    [Fact]
    public async Task A_rejected_or_repeated_submit_is_not_reported()
    {
        var sink = new FakeSink();
        var wrapped = new PresenceNotifyingAvailabilityMachine(new AvailabilityMachine("a1", AvailabilityMachineState.Off), _agent, sink);

        await wrapped.SubmitAsync(Start, Ct);
        var repeat = await wrapped.SubmitAsync(Start, Ct);

        Assert.False(repeat.Applied);
        Assert.Single(sink.Calls);
    }

    [Fact]
    public async Task A_failing_sink_does_not_fail_the_submit_and_is_handed_to_the_failure_hook()
    {
        var failures = new List<Exception>();
        var machine = new AvailabilityMachine("a1", AvailabilityMachineState.Off);
        var wrapped = new PresenceNotifyingAvailabilityMachine(machine, _agent, new FakeSink(fail: true), onSinkFailure: failures.Add);

        var transition = await wrapped.SubmitAsync(Start, Ct);

        Assert.True(transition.Applied);
        Assert.Equal(AvailabilityMachineState.OnShift, machine.Snapshot.State);
        Assert.IsType<InvalidOperationException>(Assert.Single(failures));
    }

    [Fact]
    public async Task With_no_sink_the_submit_is_a_plain_submit()
    {
        var wrapped = new PresenceNotifyingAvailabilityMachine(new AvailabilityMachine("a1", AvailabilityMachineState.Off), _agent);

        Assert.True((await wrapped.SubmitAsync(Start, Ct)).Applied);
    }
}
