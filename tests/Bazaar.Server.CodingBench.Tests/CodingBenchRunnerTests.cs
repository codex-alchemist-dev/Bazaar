using Bazaar.Server.CodingBench.Abstractions;

namespace Bazaar.Server.CodingBench.Tests;

public sealed class CodingBenchRunnerTests
{
    private static CodingBenchRunner Build(
        IEnumerable<CodingTask> tasks,
        FakeAgentRunner agent,
        FakeVerifier verifier,
        ICodingBenchApproval? approval = null,
        TimeProvider? clock = null) =>
        new(new StaticCodingBenchTaskSource(tasks), agent, verifier, approval ?? new FixedCodingBenchApproval(true), clock);

    [Fact]
    public async Task Refuses_without_approval_and_runs_nothing()
    {
        var agent = new FakeAgentRunner();
        var verifier = new FakeVerifier();
        var runner = Build(FakeTasks.Make(3), agent, verifier, new DenyUntilApprovedCodingBenchApproval());

        await Assert.ThrowsAsync<CodingBenchNotApprovedException>(() =>
            runner.RunAsync(new CodingBenchOptions(["model-a"]), CancellationToken.None));

        Assert.Equal(0, agent.Calls);
        Assert.Equal(0, verifier.Calls);
    }

    [Fact]
    public async Task Fixed_refusal_runs_nothing()
    {
        var agent = new FakeAgentRunner();
        var verifier = new FakeVerifier();
        var runner = Build(FakeTasks.Make(3), agent, verifier, new FixedCodingBenchApproval(false));

        await Assert.ThrowsAsync<CodingBenchNotApprovedException>(() =>
            runner.RunAsync(new CodingBenchOptions(["model-a"]), CancellationToken.None));

        Assert.Equal(0, agent.Calls);
    }

    [Fact]
    public async Task Forty_tasks_three_formats_three_seeds_is_360_runs_for_one_model()
    {
        var agent = new FakeAgentRunner();
        var verifier = new FakeVerifier();
        var runner = Build(FakeTasks.Make(40), agent, verifier);

        var report = await runner.RunAsync(new CodingBenchOptions(["model-a"]), CancellationToken.None);

        Assert.Equal(360, report.Results.Count);
        Assert.Equal(360, agent.Calls);
        Assert.Equal(360, verifier.Calls);
        Assert.Equal(360, report.Results.Select(r => (r.Request.Format, r.Request.Seed, r.Request.Task.Id)).Distinct().Count());
    }

    [Fact]
    public async Task Quota_caps_the_runs_of_one_model()
    {
        var agent = new FakeAgentRunner();
        var runner = Build(FakeTasks.Make(40), agent, new FakeVerifier());

        var report = await runner.RunAsync(new CodingBenchOptions(["model-a"], MaxRunsPerModel: 10), CancellationToken.None);

        Assert.Equal(10, report.Results.Count);
        Assert.Equal(10, agent.Calls);
    }

    [Fact]
    public async Task Quota_applies_per_model_with_two_models()
    {
        var agent = new FakeAgentRunner();
        var runner = Build(FakeTasks.Make(40), agent, new FakeVerifier());

        var report = await runner.RunAsync(new CodingBenchOptions(["model-a", "model-b"], MaxRunsPerModel: 10), CancellationToken.None);

        Assert.Equal(20, report.Results.Count);
        Assert.Equal(10, report.Results.Count(r => r.Request.Model == "model-a"));
        Assert.Equal(10, report.Results.Count(r => r.Request.Model == "model-b"));
        Assert.Equal(20, agent.Calls);
    }

    [Fact]
    public async Task Parallelism_never_exceeds_MaxParallelRuns()
    {
        var agent = new FakeAgentRunner(delay: TimeSpan.FromMilliseconds(5));
        var runner = Build(FakeTasks.Make(20), agent, new FakeVerifier());

        var report = await runner.RunAsync(
            new CodingBenchOptions(["model-a"], Formats: [BenchEditFormat.WholeFile], Seeds: [1], MaxParallelRuns: 3),
            CancellationToken.None);

        Assert.Equal(20, report.Results.Count);
        Assert.InRange(agent.MaxConcurrent, 1, 3);
    }

    [Fact]
    public async Task A_throwing_agent_run_is_recorded_not_fatal()
    {
        var agent = new FakeAgentRunner(behaviour: request =>
            request.Task.Id == "task-001"
                ? throw new InvalidOperationException("sandbox lost")
                : Task.FromResult(FakeOutcomes.Clean()));
        var verifier = new FakeVerifier();
        var runner = Build(FakeTasks.Make(3), agent, verifier);

        var report = await runner.RunAsync(
            new CodingBenchOptions(["model-a"], Formats: [BenchEditFormat.WholeFile], Seeds: [1]),
            CancellationToken.None);

        Assert.Equal(3, report.Results.Count);
        var failed = report.Results.Single(r => r.Request.Task.Id == "task-001");
        Assert.Equal("error", failed.Outcome.CostSource);
        Assert.False(failed.Outcome.ClaimedSuccess);
        Assert.False(failed.Outcome.TimedOut);
        Assert.False(failed.VerifierPassed);
        Assert.False(failed.Correct);
        Assert.False(failed.FakeSuccess);
        Assert.Equal(2, verifier.Calls);
    }

    [Fact]
    public async Task Cancellation_propagates()
    {
        using var cts = new CancellationTokenSource();
        var agent = new FakeAgentRunner(behaviour: async request =>
        {
            await cts.CancelAsync();
            await Task.Delay(TimeSpan.FromSeconds(5), CancellationToken.None).ConfigureAwait(false);
            return FakeOutcomes.Clean();
        });
        var runner = Build(FakeTasks.Make(10), agent, new FakeVerifier());

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            runner.RunAsync(new CodingBenchOptions(["model-a"], Formats: [BenchEditFormat.WholeFile], Seeds: [1], MaxParallelRuns: 1), cts.Token));
    }

    [Fact]
    public async Task Results_are_ordered_by_model_format_seed_and_task_id()
    {
        var agent = new FakeAgentRunner(delay: TimeSpan.FromMilliseconds(2));
        var runner = Build(FakeTasks.Make(4).AsEnumerable().Reverse(), agent, new FakeVerifier());

        var report = await runner.RunAsync(
            new CodingBenchOptions(
                ["model-b", "model-a"],
                Formats: [BenchEditFormat.SearchReplace, BenchEditFormat.WholeFile],
                Seeds: [2, 1],
                MaxParallelRuns: 4),
            CancellationToken.None);

        var keys = report.Results.Select(r => (r.Request.Model, r.Request.Format, r.Request.Seed, r.Request.Task.Id)).ToList();
        var expected = keys.OrderBy(k => k.Model, StringComparer.Ordinal)
            .ThenBy(k => k.Format)
            .ThenBy(k => k.Seed)
            .ThenBy(k => k.Id, StringComparer.Ordinal)
            .ToList();
        Assert.Equal(expected, keys);
        Assert.Equal("model-a", keys[0].Model);
        Assert.Equal(BenchEditFormat.WholeFile, keys[0].Format);
        Assert.Equal(1, keys[0].Seed);
        Assert.Equal("task-000", keys[0].Id);
    }

    [Fact]
    public async Task Duration_comes_from_the_TimeProvider()
    {
        var agent = new FakeAgentRunner();
        var clock = new SteppedTimeProvider(step: 500);
        var runner = Build(FakeTasks.Make(2), agent, new FakeVerifier(), clock: clock);

        var report = await runner.RunAsync(
            new CodingBenchOptions(["model-a"], Formats: [BenchEditFormat.WholeFile], Seeds: [1], MaxParallelRuns: 1),
            CancellationToken.None);

        Assert.All(report.Results, r => Assert.Equal(TimeSpan.FromMilliseconds(500), r.Duration));
    }

    [Fact]
    public async Task Report_carries_summaries_and_a_recommended_format_per_model()
    {
        var runner = Build(FakeTasks.Make(6), new FakeAgentRunner(), new FakeVerifier());

        var report = await runner.RunAsync(new CodingBenchOptions(["model-a"]), CancellationToken.None);

        Assert.NotEmpty(report.Summaries);
        Assert.Equal(report.Results.Count, report.Summaries.Sum(s => s.Runs));
        Assert.True(report.RecommendedFormat.ContainsKey("model-a"));
        Assert.False(string.IsNullOrWhiteSpace(report.RunId));
    }

    [Theory]
    [InlineData(0, 1)]
    [InlineData(10, 0)]
    public async Task Rejects_invalid_parallelism_or_quota(int quota, int parallel)
    {
        var runner = Build(FakeTasks.Make(2), new FakeAgentRunner(), new FakeVerifier());
        var options = new CodingBenchOptions(["model-a"], MaxRunsPerModel: quota, MaxParallelRuns: parallel);

        if (parallel < 1)
        {
            await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => runner.RunAsync(options, CancellationToken.None));
        }
        else
        {
            var report = await runner.RunAsync(options, CancellationToken.None);
            Assert.Empty(report.Results);
        }
    }
}
