using Bazaar.Server.AgentSafety.Anomaly;
using Bazaar.Testing;

namespace Bazaar.Server.AgentSafety.Tests.Anomaly;

public sealed class ChannelAnomalyAlertSourceTests
{
    private static AnomalyAlert Alert(string ruleId) =>
        new(ruleId, "agent-a", null, AnomalySeverity.Low, FakeClock.DefaultStart, "detail");

    [Fact]
    public async Task Alerts_are_read_in_publish_order()
    {
        var source = new ChannelAnomalyAlertSource(capacity: 10);
        await source.PublishAsync(Alert("one"), TestContext.Current.CancellationToken);
        await source.PublishAsync(Alert("two"), TestContext.Current.CancellationToken);
        await source.PublishAsync(Alert("three"), TestContext.Current.CancellationToken);

        var read = await ReadAsync(source, 3);

        Assert.Equal(["one", "two", "three"], read.Select(a => a.RuleId));
        Assert.Equal(0, source.DroppedCount);
    }

    [Fact]
    public async Task A_full_queue_drops_the_oldest_alert_and_counts_it()
    {
        var source = new ChannelAnomalyAlertSource(capacity: 2);
        foreach (var id in new[] { "1", "2", "3", "4", "5" })
        {
            await source.PublishAsync(Alert(id), TestContext.Current.CancellationToken);
        }

        Assert.Equal(3, source.DroppedCount);
        var read = await ReadAsync(source, 2);
        Assert.Equal(["4", "5"], read.Select(a => a.RuleId));
    }

    [Fact]
    public async Task Publishing_never_waits_and_counts_every_drop_under_parallel_publishers()
    {
        const int Published = 1000;
        const int Capacity = 10;
        var source = new ChannelAnomalyAlertSource(Capacity);

        await Task.Run(() => Parallel.For(0, Published, i => source.PublishAsync(Alert(i.ToString(System.Globalization.CultureInfo.InvariantCulture)), CancellationToken.None).AsTask().Wait()), TestContext.Current.CancellationToken);

        Assert.Equal(Published - Capacity, source.DroppedCount);
        var read = await ReadAsync(source, Capacity);
        Assert.Equal(Capacity, read.Count);
    }

    [Fact]
    public async Task A_reader_waiting_on_an_empty_queue_gets_the_next_alert()
    {
        var source = new ChannelAnomalyAlertSource();
        await using var enumerator = source.ReadAllAsync(TestContext.Current.CancellationToken).GetAsyncEnumerator(TestContext.Current.CancellationToken);

        var pending = enumerator.MoveNextAsync().AsTask();
        await source.PublishAsync(Alert("late"), TestContext.Current.CancellationToken);

        Assert.True(await pending.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken));
        Assert.Equal("late", enumerator.Current.RuleId);
    }

    [Fact]
    public async Task A_flood_of_lesser_alerts_cannot_push_out_a_serious_one()
    {
        var source = new ChannelAnomalyAlertSource(capacity: 4);
        var ct = TestContext.Current.CancellationToken;
        await source.PublishAsync(new AnomalyAlert("canary_hit", "agent-a", null, AnomalySeverity.Critical, FakeClock.DefaultStart, "detail"), ct);
        for (var i = 0; i < 100; i++)
        {
            await source.PublishAsync(new AnomalyAlert("flood-" + i, "agent-b", null, AnomalySeverity.High, FakeClock.DefaultStart, "detail"), ct);
        }

        var read = await ReadAsync(source, 4);

        Assert.Equal("canary_hit", read[0].RuleId);
        Assert.Equal(97, source.DroppedCount);
    }

    [Fact]
    public async Task A_lesser_alert_arriving_at_a_full_queue_of_serious_ones_is_the_one_dropped()
    {
        var source = new ChannelAnomalyAlertSource(capacity: 2);
        var ct = TestContext.Current.CancellationToken;
        await source.PublishAsync(new AnomalyAlert("r1", "agent-a", null, AnomalySeverity.Critical, FakeClock.DefaultStart, "detail"), ct);
        await source.PublishAsync(new AnomalyAlert("r2", "agent-b", null, AnomalySeverity.Critical, FakeClock.DefaultStart, "detail"), ct);
        await source.PublishAsync(new AnomalyAlert("r3", "agent-c", null, AnomalySeverity.Low, FakeClock.DefaultStart, "detail"), ct);

        var read = await ReadAsync(source, 2);

        Assert.Equal(["r1", "r2"], read.Select(a => a.RuleId));
        Assert.Equal(1, source.DroppedCount);
    }

    [Fact]
    public async Task Repeats_for_one_agent_and_rule_merge_and_keep_the_highest_severity()
    {
        var source = new ChannelAnomalyAlertSource(capacity: 2);
        var ct = TestContext.Current.CancellationToken;
        await source.PublishAsync(new AnomalyAlert("out_of_scope_read", "agent-a", null, AnomalySeverity.Medium, FakeClock.DefaultStart, "detail"), ct);
        for (var i = 0; i < 50; i++)
        {
            await source.PublishAsync(new AnomalyAlert("out_of_scope_read", "agent-a", null, AnomalySeverity.High, FakeClock.DefaultStart, "detail"), ct);
        }
        await source.PublishAsync(Alert("other"), ct);

        var read = await ReadAsync(source, 2);

        Assert.Equal(["out_of_scope_read", "other"], read.Select(a => a.RuleId));
        Assert.Equal(AnomalySeverity.High, read[0].Severity);
        Assert.Equal(0, source.DroppedCount);
    }

    [Fact]
    public async Task An_alert_read_from_the_queue_no_longer_absorbs_the_next_one()
    {
        var source = new ChannelAnomalyAlertSource(capacity: 2);
        var ct = TestContext.Current.CancellationToken;
        await source.PublishAsync(Alert("one"), ct);
        Assert.Equal("one", Assert.Single(await ReadAsync(source, 1)).RuleId);

        await source.PublishAsync(Alert("one"), ct);

        Assert.Equal("one", Assert.Single(await ReadAsync(source, 1)).RuleId);
    }

    [Fact]
    public void Capacity_below_one_is_rejected()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new ChannelAnomalyAlertSource(0));
    }

    private static async Task<List<AnomalyAlert>> ReadAsync(ChannelAnomalyAlertSource source, int count)
    {
        var read = new List<AnomalyAlert>();
        await using var enumerator = source.ReadAllAsync(TestContext.Current.CancellationToken).GetAsyncEnumerator(TestContext.Current.CancellationToken);
        for (var i = 0; i < count; i++)
        {
            Assert.True(await enumerator.MoveNextAsync());
            read.Add(enumerator.Current);
        }

        return read;
    }
}
