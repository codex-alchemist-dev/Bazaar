using Bazaar.Server.Knowledge.Metrics;
using Microsoft.Extensions.Time.Testing;

namespace Bazaar.Server.Health.Tests;

public class BuildBrokenTests
{
    private static readonly DateTimeOffset _now = new(2026, 11, 22, 12, 0, 0, TimeSpan.Zero);
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static CiRunFact Run(string id, int minute, bool passed) => new(id, _now.AddMinutes(minute - 100), passed);

    [Fact]
    public void First_sight_is_a_baseline_and_raises_nothing()
    {
        var d = new BuildBrokenDetector();
        Assert.Empty(d.Observe("p", [Run("1", 1, true), Run("2", 2, false)]));
        Assert.Empty(d.Observe("p", [Run("3", 3, false)]));
    }

    [Fact]
    public void First_red_after_green_raises_once_per_streak()
    {
        var d = new BuildBrokenDetector();
        d.Observe("p", [Run("1", 1, true)]);

        var raised = Assert.Single(d.Observe("p", [Run("2", 2, false), Run("3", 3, false)]));
        Assert.Equal(new BuildBroken("p", "2", Run("2", 2, false).CompletedAt), raised);
        Assert.Empty(d.Observe("p", [Run("4", 4, false)]));
    }

    [Fact]
    public void Re_reading_the_same_runs_raises_nothing()
    {
        var d = new BuildBrokenDetector();
        d.Observe("p", [Run("1", 1, true)]);
        Assert.Single(d.Observe("p", [Run("2", 2, false)]));
        Assert.Empty(d.Observe("p", [Run("1", 1, true), Run("2", 2, false)]));
    }

    [Fact]
    public void A_new_streak_after_green_raises_again_and_projects_are_independent()
    {
        var d = new BuildBrokenDetector();
        d.Observe("p", [Run("1", 1, true)]);
        d.Observe("q", [Run("1", 1, true)]);
        Assert.Single(d.Observe("p", [Run("2", 2, false)]));
        Assert.Empty(d.Observe("p", [Run("3", 3, true)]));
        Assert.Equal("4", Assert.Single(d.Observe("p", [Run("4", 4, false)])).RunRef);
        Assert.Empty(d.Observe("q", [Run("2", 2, true)]));
    }

    private sealed class Listener : IBuildBrokenListener
    {
        public List<BuildBroken> Seen { get; } = [];
        public Task OnBuildBrokenAsync(BuildBroken signal, CancellationToken cancellationToken = default)
        {
            Seen.Add(signal);
            return Task.CompletedTask;
        }
    }

    private sealed class Throwing : IBuildBrokenListener
    {
        public Task OnBuildBrokenAsync(BuildBroken signal, CancellationToken cancellationToken = default) => throw new InvalidOperationException("boom");
    }

    private sealed class Projects : IHealthProjectSource
    {
        public Task<IReadOnlyList<string>> ListProjectsAsync(CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<string>>(["a"]);
    }

    private sealed class Ci(Func<IReadOnlyList<CiRunFact>> runs) : IHealthFactSource
    {
        public Task<HealthFacts> ReadAsync(string projectId, DateOnly day, CancellationToken cancellationToken = default) =>
            Task.FromResult(day == DateOnly.FromDateTime(_now.UtcDateTime) ? new HealthFacts { CiRuns = runs() } : HealthFacts.Empty);
    }

    [Fact]
    public async Task Collection_pass_notifies_listeners_once_and_a_throwing_listener_does_not_fail_it()
    {
        var runs = new List<CiRunFact> { new("1", _now.AddHours(-3), true) };
        var good = new Listener();
        var handler = new HealthCollectJobHandler(new InMemoryMetricStore(), [new Ci(() => runs)], [new Projects()],
            new FakeTimeProvider(_now), [new Throwing(), good]);

        await handler.CollectAsync(Ct); // baseline
        Assert.Empty(good.Seen);

        runs.Add(new("2", _now.AddHours(-2), false));
        await handler.CollectAsync(Ct);
        await handler.CollectAsync(Ct);

        var signal = Assert.Single(good.Seen);
        Assert.Equal("a", signal.ProjectId);
        Assert.Equal("2", signal.RunRef);
    }

    [Fact]
    public async Task Collection_without_listeners_still_works()
    {
        var handler = new HealthCollectJobHandler(new InMemoryMetricStore(), [new Ci(() => [new("1", _now, false)])], [new Projects()], new FakeTimeProvider(_now));
        await handler.CollectAsync(Ct);
    }
}
