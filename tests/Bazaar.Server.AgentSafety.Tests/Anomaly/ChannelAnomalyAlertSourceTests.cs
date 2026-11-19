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
