using Bazaar.Server.Presence.Sources.Abstractions;
using Bazaar.Server.PsychWard.Abstractions;
using Microsoft.Extensions.Time.Testing;

namespace Bazaar.Server.PsychWard.Tests;

/// <summary>The ward reports each saved admission and discharge to presence, and a sink failure never fails the transition (doc 26, doc 23).</summary>
public class PsychWardPresenceTests
{
    private static readonly Guid _a = Guid.Parse("00000000-0000-7000-8000-00000000000a");

    private sealed class FakeSink(bool fail = false) : IWardStateSink
    {
        public List<WardStay> Stays { get; } = [];

        public ValueTask OnStayChangedAsync(WardStay stay, CancellationToken ct)
        {
            Stays.Add(stay);
            return fail ? throw new InvalidOperationException("sink down") : ValueTask.CompletedTask;
        }
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static PsychWardService Ward(FakeTimeProvider time, IWardStateSink? sink, Action<Exception>? onFailure = null) =>
        new(new InMemoryWardStayStore(), new InMemoryVentStore(), new PsychWardSettings { MinStayMinutes = 0 }, time, sink, onFailure);

    [Fact]
    public async Task Admission_and_discharge_are_reported_after_they_are_saved()
    {
        var sink = new FakeSink();
        using var ward = Ward(new FakeTimeProvider(), sink);

        var admitted = await ward.AdmitAsync(_a, WardTrigger.LoopGuard, null, Ct);
        var discharged = await ward.DischargeAsync(_a, new DischargeRequest(true), Ct);

        Assert.Equal(2, sink.Stays.Count);
        Assert.True(sink.Stays[0].IsAdmitted);
        Assert.Equal(admitted.Stay!.Id, sink.Stays[0].Id);
        Assert.False(sink.Stays[1].IsAdmitted);
        Assert.Equal(discharged.Stay!.DischargedAt, sink.Stays[1].DischargedAt);
    }

    [Fact]
    public async Task A_refused_transition_and_a_check_in_are_not_reported()
    {
        var sink = new FakeSink();
        using var ward = Ward(new FakeTimeProvider(), sink);

        Assert.False((await ward.DischargeAsync(_a, new DischargeRequest(true), Ct)).Accepted);
        await ward.AdmitAsync(_a, WardTrigger.LoopGuard, null, Ct);
        Assert.False((await ward.AdmitAsync(_a, WardTrigger.LoopGuard, null, Ct)).Accepted);

        Assert.Single(sink.Stays);
    }

    [Fact]
    public async Task A_failing_sink_does_not_fail_the_transition()
    {
        var failures = new List<Exception>();
        using var ward = Ward(new FakeTimeProvider(), new FakeSink(fail: true), failures.Add);

        var admitted = await ward.AdmitAsync(_a, WardTrigger.LoopGuard, null, Ct);
        var discharged = await ward.DischargeAsync(_a, new DischargeRequest(true), Ct);

        Assert.True(admitted.Accepted);
        Assert.True(discharged.Accepted);
        Assert.Equal(2, failures.Count);
    }

    [Fact]
    public async Task With_no_sink_the_ward_works_as_before()
    {
        using var ward = Ward(new FakeTimeProvider(), sink: null);

        Assert.True((await ward.AdmitAsync(_a, WardTrigger.LoopGuard, null, Ct)).Accepted);
    }
}
