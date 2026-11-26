using Microsoft.Extensions.Time.Testing;

namespace Bazaar.Server.Gateway.Selection.Tests;

public class PersistenceAndInsightsTests
{
    private static readonly EndpointKey _a = new("anthropic-direct", "claude-haiku-5-5");
    private static readonly EndpointKey _b = new("freellmapi", "claude-haiku-5-5");

    private static void Succeed(EndpointStatsTracker stats, EndpointKey key, int ms, int times)
    {
        for (var i = 0; i < times; i++)
        {
            stats.Begin(key).Complete(new AttemptOutcome(AttemptOutcomeKind.Success, TimeSpan.FromMilliseconds(ms), TimeSpan.FromMilliseconds(ms)));
        }
    }

    [Fact]
    public void Export_then_import_restores_counters_and_latency()
    {
        var time = new FakeTimeProvider();
        var first = new EndpointStatsTracker(time: time);
        Succeed(first, _a, 250, 12);
        first.Begin(_b).Complete(new AttemptOutcome(AttemptOutcomeKind.Failure));

        var second = new EndpointStatsTracker(time: time);
        second.Import(first.Export());

        var a = second.Snapshot(_a);
        Assert.Equal(12, a.Successes, 3);
        Assert.NotNull(a.PeakEwmaLatency);
        Assert.Equal(1, second.Snapshot(_b).Failures, 3);
    }

    [Fact]
    public void Import_ignores_nonsense_values()
    {
        var stats = new EndpointStatsTracker();
        stats.Import([new EndpointStatsSnapshot(_a, double.NaN, 0, null), new EndpointStatsSnapshot(_b, -3, 0, -1)]);
        Assert.Equal(0, stats.Snapshot(_a).Samples);
        Assert.Equal(0, stats.Snapshot(_b).Samples);
    }

    [Fact]
    public void Stats_file_round_trips_and_a_missing_or_broken_file_means_no_data()
    {
        var dir = Directory.CreateTempSubdirectory("selection-stats");
        try
        {
            var path = Path.Combine(dir.FullName, "stats.json");
            Assert.Empty(StatsFile.Load(path));

            StatsFile.Save(path, [new EndpointStatsSnapshot(_a, 4, 1, 120)]);
            var loaded = Assert.Single(StatsFile.Load(path));
            Assert.Equal((_a, 4.0, 1.0, 120.0), (loaded.Key, loaded.Successes, loaded.Failures, loaded.PeakEwmaMs));

            File.WriteAllText(path, "{not json");
            Assert.Empty(StatsFile.Load(path));
        }
        finally
        {
            dir.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task Insights_record_decisions_newest_first_and_stay_bounded()
    {
        var ct = TestContext.Current.CancellationToken;
        var time = new FakeTimeProvider();
        var stats = new EndpointStatsTracker(time: time);
        var insights = new SelectionInsights(stats, time, capacity: 3);
        var ranker = new ProfileBoundRanker(new EndpointSelector(stats, time: time, random: new SeededSelectionRandom(1)), insights);
        await ranker.BindAsync("claude", "balanced", ct);
        Succeed(stats, _a, 200, 3);

        for (var i = 0; i < 5; i++)
        {
            time.Advance(TimeSpan.FromSeconds(1));
            ranker.Order("claude", [new Endpoint(_a), new Endpoint(_b) { Identity = "pooled" }], new SelectionRequest());
        }

        var recent = insights.Recent();
        Assert.Equal(3, recent.Count);
        Assert.True(recent[0].At > recent[1].At);
        Assert.Equal("claude", recent[0].Route);
        Assert.Equal(2, recent[0].Ranking.Count);
        Assert.Contains(insights.Endpoints(), e => e.Key == _a && e.Successes > 0);
    }

    [Fact]
    public void An_unbound_route_records_nothing()
    {
        var stats = new EndpointStatsTracker();
        var insights = new SelectionInsights(stats);
        var ranker = new ProfileBoundRanker(new EndpointSelector(stats), insights);
        Assert.Null(ranker.Order("other", [new Endpoint(_a)], new SelectionRequest()));
        Assert.Empty(insights.Recent());
    }
}
