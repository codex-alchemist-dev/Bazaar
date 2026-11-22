using Bazaar.Server.VmDensity.Abstractions;
using Microsoft.Extensions.Time.Testing;

namespace Bazaar.Server.VmBench.Display.Tests;

public sealed class DensityRampTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Counts_pairs_admitted_before_the_governor_queues_and_records_host_free_memory()
    {
        var time = new FakeTimeProvider();
        var governor = new FakeGovernor(time, admitCount: 6) { HostFreeMb = 4321 };

        var r = Assert.Single(await new DensityRampScenario(governor).RunAsync(Bench.Context(time), Ct));

        Assert.Equal(DisplayBenchOutcome.Passed, r.Outcome);
        Assert.Equal(3, r.Value);
        Assert.Equal(2, r.Threshold);
        Assert.Contains(r.Metrics, m => m.Name == "hostFreeMb" && m.Value == 4321);
        Assert.Equal(7, governor.Requests.Count);
        Assert.Equal(DensityGuestOs.Windows, governor.Requests[0].Os);
        Assert.Equal(DensityGuestOs.Linux, governor.Requests[1].Os);
        Assert.Equal(4096, governor.Requests[0].MemoryMb);
        Assert.Equal(2048, governor.Requests[1].MemoryMb);
        Assert.Equal(governor.Requests[0].AgentId, governor.Requests[1].AgentId);
        Assert.All(governor.Tickets, t => Assert.True(t.Disposed));
    }

    [Theory]
    [InlineData(4, 2, DisplayBenchOutcome.Passed)]
    [InlineData(5, 2, DisplayBenchOutcome.Passed)]
    [InlineData(3, 1, DisplayBenchOutcome.Failed)]
    [InlineData(2, 1, DisplayBenchOutcome.Failed)]
    public async Task Verdict_at_the_minimum_pairs_edge_and_half_a_pair_does_not_count(int admittedVms, int pairs, DisplayBenchOutcome expected)
    {
        var time = new FakeTimeProvider();
        var governor = new FakeGovernor(time, admittedVms);

        var r = Assert.Single(await new DensityRampScenario(governor).RunAsync(Bench.Context(time, Bench.Criteria(c => c.DensityMinPairs = 2)), Ct));

        Assert.Equal(pairs, r.Value);
        Assert.Equal(expected, r.Outcome);
        Assert.All(governor.Tickets, t => Assert.True(t.Disposed));
    }

    [Fact]
    public async Task Stops_at_the_pair_ceiling_when_the_governor_never_queues()
    {
        var time = new FakeTimeProvider();
        var governor = new FakeGovernor(time, admitCount: int.MaxValue);

        var r = Assert.Single(await new DensityRampScenario(governor).RunAsync(Bench.Context(time, Bench.Criteria(c => c.DensityMaxPairs = 4)), Ct));

        Assert.Equal(4, r.Value);
        Assert.Equal(DisplayBenchOutcome.Passed, r.Outcome);
        Assert.Equal(8, governor.Requests.Count);
    }

    [Fact]
    public async Task A_refusal_ends_the_ramp_and_is_reported_in_the_reason()
    {
        var time = new FakeTimeProvider();
        var governor = new FakeGovernor(time, int.MaxValue)
        {
            RefuseIndex = 2,
            RefuseAt = new DensityAdmissionException(DensityAdmissionFailure.QueueFull, "full"),
        };

        var r = Assert.Single(await new DensityRampScenario(governor).RunAsync(Bench.Context(time, Bench.Criteria(c => c.DensityMinPairs = 3)), Ct));

        Assert.Equal(1, r.Value);
        Assert.Equal(DisplayBenchOutcome.Failed, r.Outcome);
        Assert.Contains("QueueFull", r.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Is_not_run_without_a_governor()
    {
        var r = Assert.Single(await new DensityRampScenario().RunAsync(Bench.Context(new FakeTimeProvider()), Ct));

        Assert.Equal(DisplayBenchOutcome.NotRun, r.Outcome);
        Assert.Null(r.Value);
        Assert.Contains("governor", r.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_governor_error_fails_the_scenario_and_releases_reservations()
    {
        var time = new FakeTimeProvider();
        var governor = new ThrowingGovernor(time);

        var r = Assert.Single(await new DensityRampScenario(governor).RunAsync(Bench.Context(time), Ct));

        Assert.Equal(DisplayBenchOutcome.Failed, r.Outcome);
        Assert.Contains("probe exploded", r.Reason, StringComparison.Ordinal);
        Assert.All(governor.Tickets, t => Assert.True(t.Disposed));
        Assert.NotEmpty(governor.Tickets);
    }

    [Fact]
    public async Task Honours_cancellation_and_releases_reservations()
    {
        var time = new FakeTimeProvider();
        using var cts = new CancellationTokenSource();
        var governor = new CancellingGovernor(cts);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new DensityRampScenario(governor).RunAsync(Bench.Context(time), cts.Token));

        Assert.All(governor.Tickets, t => Assert.True(t.Disposed));
        Assert.NotEmpty(governor.Tickets);
    }

    private sealed class ThrowingGovernor : IDensityGovernor
    {
        private readonly FakeGovernor _inner;

        public ThrowingGovernor(FakeTimeProvider time) => _inner = new FakeGovernor(time, 2);

        public List<FakeTicket> Tickets => _inner.Tickets;

        public Task<AdmissionTicket> AdmitAsync(DensityVm vm, CancellationToken cancellationToken) =>
            _inner.Requests.Count >= 2 ? throw new InvalidOperationException("probe exploded") : _inner.AdmitAsync(vm, cancellationToken);

        public DensitySnapshot Snapshot() => _inner.Snapshot();

        public void NotifyMemoryChanged() => _inner.NotifyMemoryChanged();
    }

    private sealed class CancellingGovernor : IDensityGovernor
    {
        private readonly CancellationTokenSource _cts;

        public CancellingGovernor(CancellationTokenSource cts) => _cts = cts;

        public List<FakeTicket> Tickets { get; } = [];

        public Task<AdmissionTicket> AdmitAsync(DensityVm vm, CancellationToken cancellationToken)
        {
            if (Tickets.Count == 1)
            {
                _cts.Cancel();
                return Task.FromCanceled<AdmissionTicket>(_cts.Token);
            }

            var t = new FakeTicket();
            Tickets.Add(t);
            return Task.FromResult<AdmissionTicket>(t);
        }

        public DensitySnapshot Snapshot() => new(0, 0, 0, 0, []);

        public void NotifyMemoryChanged()
        {
        }
    }
}
