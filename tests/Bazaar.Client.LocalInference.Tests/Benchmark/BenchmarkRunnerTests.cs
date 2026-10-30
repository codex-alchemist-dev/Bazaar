using Bazaar.Client.LocalInference.Benchmark;

namespace Bazaar.Client.LocalInference.Tests.Benchmark;

public class BenchmarkRunnerTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 30, 9, 0, 0, TimeSpan.Zero);

    private static CorpusItem Item(string id, params string[] mustKeep) => new(id, $"input {id}", mustKeep);

    [Fact]
    public async Task Failing_backend_is_recorded_as_error_rate_and_never_thrown()
    {
        var corpus = new[] { Item("a", "1"), Item("b", "2"), Item("c", "3"), Item("d", "4") };
        var backend = new FakeLocalBackend(BackendTier.Cpu)
            .Throw(new TimeoutException("slow"))
            .Reply("text 2")
            .Throw(new InsufficientMemoryException("full"))
            .Reply("text 4");

        var report = await new BenchmarkRunner(new ScriptedTimeProvider(Now)).RunAsync([backend], corpus, CancellationToken.None);

        Assert.Equal(4, report.Runs);
        Assert.Equal(2, report.Errors);
        Assert.Equal(0.5, report.ErrorRate);
        Assert.StartsWith("TimeoutException", report.Records[0].Error, StringComparison.Ordinal);
        Assert.Null(report.Records[1].Error);
    }

    [Fact]
    public async Task Failed_run_does_not_pass_the_guard_and_is_not_degraded()
    {
        var backend = new FakeLocalBackend(BackendTier.Npu).Throw(new InvalidOperationException("blob mismatch"));

        var report = await new BenchmarkRunner(new ScriptedTimeProvider(Now)).RunAsync([backend], [Item("a", "7")], CancellationToken.None);

        var record = Assert.Single(report.Records);
        Assert.Equal(BackendTier.Npu, record.Tier);
        Assert.False(record.GuardPassed);
        Assert.False(record.Degraded);
        Assert.Equal(0, report.GuardPassed);
    }

    [Fact]
    public async Task Guard_passes_only_when_every_mustKeep_token_survives()
    {
        var corpus = new[] { Item("kept", "14", "3:30"), Item("dropped", "14", "3:30") };
        var backend = new FakeLocalBackend(BackendTier.Cpu)
            .Reply("Review on the 14th at 3:30.")
            .Reply("Review on the 14th.");

        var report = await new BenchmarkRunner(new ScriptedTimeProvider(Now)).RunAsync([backend], corpus, CancellationToken.None);

        Assert.True(report.Records[0].GuardPassed);
        Assert.False(report.Records[1].GuardPassed);
        Assert.Equal(1, report.GuardPassed);
        Assert.Equal(0, report.Errors);
    }

    [Fact]
    public async Task Records_carry_the_tier_and_degraded_flag_the_run_reported()
    {
        var backend = new FakeLocalBackend(BackendTier.Cpu).Reply("ok", degraded: true);

        var report = await new BenchmarkRunner(new ScriptedTimeProvider(Now)).RunAsync([backend], [Item("a")], CancellationToken.None);

        var record = Assert.Single(report.Records);
        Assert.Equal(BackendTier.Cpu, record.Tier);
        Assert.True(record.Degraded);
    }

    [Fact]
    public async Task Elapsed_time_comes_from_the_injected_time_source()
    {
        var time = new ScriptedTimeProvider(Now, timestamps: [0, 15, 100, 140]);
        var backend = new FakeLocalBackend(BackendTier.Cpu).Reply("a").Reply("b");
        double[] expected = [15, 40];

        var report = await new BenchmarkRunner(time).RunAsync([backend], [Item("a"), Item("b")], CancellationToken.None);

        Assert.Equal(expected, report.Records.Select(record => record.ElapsedMs));
        Assert.Equal(15.0, report.P50Ms);
        Assert.Equal(40.0, report.P95Ms);
        Assert.Equal(Now, report.GeneratedAt);
    }

    [Fact]
    public async Task Per_tier_counts_and_percentiles_are_reported_for_each_backend()
    {
        var time = new ScriptedTimeProvider(Now, timestamps: [0, 10, 0, 30, 0, 20, 0, 40]);
        var npu = new FakeLocalBackend(BackendTier.Npu).Reply("x", degraded: true).Throw(new TimeoutException());
        var cpu = new FakeLocalBackend(BackendTier.Cpu).Reply("x").Reply("y");
        var corpus = new[] { Item("a"), Item("b") };

        var report = await new BenchmarkRunner(time).RunAsync([npu, cpu], corpus, CancellationToken.None);

        Assert.Equal(2, report.Tiers.Count);
        var npuSummary = report.Tiers.Single(tier => tier.Tier == BackendTier.Npu);
        Assert.Equal(2, npuSummary.Runs);
        Assert.Equal(1, npuSummary.Errors);
        Assert.Equal(1, npuSummary.Degraded);
        Assert.Equal(10.0, npuSummary.P50Ms);
        Assert.Equal(30.0, npuSummary.P95Ms);
        var cpuSummary = report.Tiers.Single(tier => tier.Tier == BackendTier.Cpu);
        Assert.Equal(2, cpuSummary.Runs);
        Assert.Equal(0, cpuSummary.Errors);
        Assert.Equal(20.0, cpuSummary.P50Ms);
        Assert.Equal(40.0, cpuSummary.P95Ms);
    }

    [Fact]
    public async Task Empty_corpus_gives_an_empty_report_without_errors()
    {
        var backend = new FakeLocalBackend(BackendTier.Cpu);
        CorpusItem[] corpus = [];

        var report = await new BenchmarkRunner(new ScriptedTimeProvider(Now)).RunAsync([backend], corpus, CancellationToken.None);

        Assert.Equal(0, report.Runs);
        Assert.Equal(0, report.ErrorRate);
        Assert.Null(report.P50Ms);
        Assert.Empty(report.Tiers);
        Assert.Equal(0, backend.CallCount);
    }

    [Fact]
    public async Task Cancellation_propagates_instead_of_being_recorded()
    {
        var backend = new FakeLocalBackend(BackendTier.Cpu).Reply("never");
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            new BenchmarkRunner(new ScriptedTimeProvider(Now)).RunAsync([backend], [Item("a")], cts.Token));
    }

    /// <summary>Clock whose timestamps are scripted in call order; the frequency is 1000 ticks per second, so ticks are milliseconds.</summary>
    private sealed class ScriptedTimeProvider : TimeProvider
    {
        private readonly DateTimeOffset _now;
        private readonly Queue<long> _timestamps;

        public ScriptedTimeProvider(DateTimeOffset now, long[]? timestamps = null)
        {
            _now = now;
            _timestamps = new Queue<long>(timestamps ?? []);
        }

        public override long TimestampFrequency => 1000;

        public override DateTimeOffset GetUtcNow() => _now;

        public override long GetTimestamp() => _timestamps.Count == 0 ? 0 : _timestamps.Dequeue();
    }
}
