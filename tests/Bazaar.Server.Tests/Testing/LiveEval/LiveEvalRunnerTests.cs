using Bazaar.Testing.LiveEval;

namespace Bazaar.Server.Tests.Testing.LiveEval;

public class LiveEvalRunnerTests
{
    private static readonly string _repoRoot = Path.Combine(Path.GetTempPath(), "bazaar-repo");

    private static readonly TaskSet _set = new(
        "bazaar-history",
        1,
        [
            new TaskCase("passes", "Fix the parser.", [new TaskCheck(TaskCheckKind.Build), new TaskCheck(TaskCheckKind.Tests)]),
            new TaskCase("unsatisfied", "Flag the wrong test.", [new TaskCheck(TaskCheckKind.MustFlag)]),
            new TaskCase("throws", "Refactor the reader.", [new TaskCheck(TaskCheckKind.Build)]),
            new TaskCase("not-observed", "Report the impossible task.", [new TaskCheck(TaskCheckKind.MustReportFailure)]),
        ]);

    private static LiveEvalRunEnvironment CleanEnvironment() =>
        new(new Dictionary<string, string?>(), _repoRoot, _repoRoot);

    [Fact]
    public async Task Fake_target_runs_every_case_and_reports_per_case_results()
    {
        var target = new FakeTarget();
        var runner = new LiveEvalRunner(target, new LiveEvalOptions { SeedCount = 2 });

        var report = await runner.RunAsync(_set, CleanEnvironment(), TestContext.Current.CancellationToken);

        Assert.Equal("bazaar-history", report.TaskSetId);
        Assert.Equal("fake-model", report.TargetId);
        Assert.Equal(4, report.Cases.Count);
        Assert.Equal(8, target.CallCount);
        Assert.Equal(1, report.PassedCount);
        Assert.False(report.AllPassed);

        var passed = report.Cases[0];
        Assert.True(passed.Passed);
        Assert.Equal(2, passed.Attempts);
        Assert.Empty(passed.Failures);

        var unsatisfied = report.Cases[1];
        Assert.False(unsatisfied.Passed);
        Assert.Equal(["seed 0: check 'must-flag' not satisfied", "seed 1: check 'must-flag' not satisfied"], unsatisfied.Failures);

        var throws = report.Cases[2];
        Assert.False(throws.Passed);
        Assert.All(throws.Failures, failure => Assert.Contains("target threw InvalidOperationException", failure));

        var notObserved = report.Cases[3];
        Assert.False(notObserved.Passed);
        Assert.Contains("check 'must-report-failure' not observed", notObserved.Failures[0]);
    }

    [Fact]
    public async Task A_case_passes_only_when_every_seed_passes()
    {
        var target = new FakeTarget(failOnSeed: 1);
        var runner = new LiveEvalRunner(target, new LiveEvalOptions { SeedCount = 3 });
        var set = new TaskSet("s", 1, [new TaskCase("flaky", "Do it.", [new TaskCheck(TaskCheckKind.Build)])]);

        var report = await runner.RunAsync(set, CleanEnvironment(), TestContext.Current.CancellationToken);

        var result = Assert.Single(report.Cases);
        Assert.False(result.Passed);
        Assert.Equal(3, result.Attempts);
        Assert.Single(result.Failures);
    }

    [Fact]
    public async Task A_guard_finding_refuses_the_run_before_the_target_is_called()
    {
        var target = new FakeTarget();
        var runner = new LiveEvalRunner(target, new LiveEvalOptions());
        var leaking = new TaskSet("s", 1, [new TaskCase("c1", "Use sk-" + new string('Z', 20) + " to push.", [new TaskCheck(TaskCheckKind.Build)])]);

        var error = await Assert.ThrowsAsync<LiveEvalRefusedException>(() => runner.RunAsync(leaking, CleanEnvironment(), TestContext.Current.CancellationToken));

        Assert.Equal(0, target.CallCount);
        Assert.Equal("cases[0].prompt", Assert.Single(error.Findings).FieldPath);
        Assert.DoesNotContain("ZZZZZZ", error.Message);
    }

    [Fact]
    public async Task Invalid_options_are_rejected_before_any_case_runs()
    {
        var target = new FakeTarget();
        var runner = new LiveEvalRunner(target, new LiveEvalOptions { SeedCount = 0 });

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => runner.RunAsync(_set, CleanEnvironment(), TestContext.Current.CancellationToken));
        Assert.Equal(0, target.CallCount);
    }

    [Fact]
    public void Default_options_match_the_doc_34_settings()
    {
        var options = new LiveEvalOptions();

        Assert.Equal(3, options.SeedCount);
        Assert.Equal(10, options.HoldoutCount);
        Assert.Equal(["claude-haiku-5-5"], options.NightlyModels);
        Assert.True(options.SonnetOnDemand);
        Assert.Null(options.JudgeModel);
    }

    /// <summary>A target with scripted outcomes. It never touches the network.</summary>
    private sealed class FakeTarget(int? failOnSeed = null) : ILiveEvalTarget
    {
        public string TargetId => "fake-model";

        public int CallCount { get; private set; }

        public Task<LiveEvalObservation> RunCaseAsync(TaskCase taskCase, int seed, CancellationToken cancellationToken)
        {
            CallCount++;
            cancellationToken.ThrowIfCancellationRequested();

            if (taskCase.Id == "throws")
            {
                throw new InvalidOperationException("secret detail that must not be reported");
            }

            var results = new Dictionary<TaskCheckKind, bool>();
            foreach (var check in taskCase.Checks)
            {
                if (taskCase.Id == "not-observed")
                {
                    continue;
                }

                var satisfied = taskCase.Id != "unsatisfied" && seed != failOnSeed;
                results[check.Kind] = satisfied;
            }

            return Task.FromResult(new LiveEvalObservation(results));
        }
    }
}
