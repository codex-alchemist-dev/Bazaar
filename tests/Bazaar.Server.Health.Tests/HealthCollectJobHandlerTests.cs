using Bazaar.Server.Jobs;
using Bazaar.Server.Knowledge.Metrics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;

namespace Bazaar.Server.Health.Tests;

public class HealthCollectJobHandlerTests
{
    private static readonly DateTimeOffset _now = new(2026, 11, 22, 12, 0, 0, TimeSpan.Zero);
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private sealed class Projects(params string[] ids) : IHealthProjectSource
    {
        public Task<IReadOnlyList<string>> ListProjectsAsync(CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<string>>(ids);
    }

    private sealed class Source(Func<string, DateOnly, HealthFacts> read) : IHealthFactSource
    {
        public Task<HealthFacts> ReadAsync(string projectId, DateOnly day, CancellationToken cancellationToken = default) => Task.FromResult(read(projectId, day));
    }

    private static HealthCollectJobHandler Make(InMemoryMetricStore store, IHealthProjectSource[] projects, IHealthFactSource[] sources) =>
        new(store, sources, projects, new FakeTimeProvider(_now));

    private static HealthFacts RevertsOn(DateTimeOffset at, string sha) => new() { Reverts = [new(sha, at)] };

    [Fact]
    public async Task Collects_today_and_yesterday_for_every_project_and_is_idempotent()
    {
        var store = new InMemoryMetricStore();
        var handler = Make(store, [new Projects("a", "b")], [new Source((p, d) => RevertsOn(d.ToDateTime(new TimeOnly(9, 0)), p))]);

        Assert.Equal(4, await handler.CollectAsync(Ct));
        Assert.Equal(0, await handler.CollectAsync(Ct));

        var today = DateOnly.FromDateTime(_now.UtcDateTime);
        var rows = await store.QueryAsync("a", HealthMetricNames.Reverts, today.AddDays(-1), today, Ct);
        Assert.Equal(2, rows.Count);
    }

    [Fact]
    public async Task Merges_what_several_sources_report()
    {
        var store = new InMemoryMetricStore();
        var at = _now;
        var handler = Make(store, [new Projects("a")],
        [
            new Source((_, d) => d == DateOnly.FromDateTime(at.UtcDateTime) ? RevertsOn(at, "x") : HealthFacts.Empty),
            new Source((_, d) => d == DateOnly.FromDateTime(at.UtcDateTime) ? new HealthFacts { Reopens = [new("c1", at)] } : HealthFacts.Empty),
        ]);

        Assert.Equal(2, await handler.CollectAsync(Ct));
    }

    [Fact]
    public async Task No_project_source_means_nothing_happens()
    {
        var handler = Make(new InMemoryMetricStore(), [], [new Source((_, _) => throw new InvalidOperationException("must not be read"))]);

        Assert.Equal(0, await handler.CollectAsync(Ct));
    }

    [Fact]
    public async Task One_failing_project_does_not_stop_the_others_but_fails_the_job()
    {
        var store = new InMemoryMetricStore();
        var handler = Make(store, [new Projects("bad", "good")], [new Source((p, d) =>
            p == "bad" ? throw new InvalidOperationException("forge down") : RevertsOn(d.ToDateTime(new TimeOnly(9, 0)), "s"))]);

        var ex = await Assert.ThrowsAsync<AggregateException>(() => handler.CollectAsync(Ct));
        Assert.Equal(2, ex.InnerExceptions.Count);

        var today = DateOnly.FromDateTime(_now.UtcDateTime);
        Assert.NotEmpty(await store.QueryAsync("good", HealthMetricNames.Reverts, today.AddDays(-1), today, Ct));
    }

    [Fact]
    public void Component_registers_the_job_the_handler_and_the_in_memory_store_without_a_database()
    {
        var services = new ServiceCollection().AddBazaarHealth();
        using var sp = services.BuildServiceProvider();

        Assert.Contains(sp.GetServices<IJobHandler>(), h => h.Kind == HealthCollectJobHandler.JobKind);
        var job = Assert.Single(sp.GetServices<RecurringJob>(), j => j.Kind == HealthCollectJobHandler.JobKind);
        Assert.Equal(TimeSpan.FromHours(1), job.Interval);
        Assert.IsType<InMemoryMetricStore>(sp.GetRequiredService<IMetricStore>());
    }
}
