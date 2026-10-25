using Bazaar.Contracts.Gateway;
using Bazaar.Server.Gateway.Usage;
using Microsoft.Extensions.Time.Testing;

namespace Bazaar.Server.Tests.Gateway.Usage;

public class UsageRecorderTests
{
    private static UsageAccumulator Finished(string agentId = "agent-1")
    {
        var accumulator = new UsageAccumulator(agentId, null, "anthropic-direct", "keyref-1", "claude-haiku-5-5",
            GatewayIdentity.Native, new FakeTimeProvider(DateTimeOffset.UnixEpoch));
        accumulator.Observe(new ModelEvent.Done(StopReason.EndTurn, "claude-haiku-5-5"));
        return accumulator;
    }

    [Fact]
    public async Task Recording_appends_exactly_one_row()
    {
        var store = new InMemoryUsageStore();
        var recorder = new UsageRecorder(store);

        await recorder.RecordAsync(Finished("agent-7"), null, TestContext.Current.CancellationToken);

        var row = Assert.Single(store.Records);
        Assert.Equal("agent-7", row.AgentId);
        Assert.Equal(UsageStatus.Success, row.Status);
    }

    [Fact]
    public async Task A_second_record_on_the_same_accumulator_throws_and_appends_nothing()
    {
        var store = new InMemoryUsageStore();
        var recorder = new UsageRecorder(store);
        var accumulator = Finished();
        await recorder.RecordAsync(accumulator, null, TestContext.Current.CancellationToken);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            recorder.RecordAsync(accumulator, null, TestContext.Current.CancellationToken).AsTask());

        Assert.Single(store.Records);
    }

    [Fact]
    public async Task A_failed_append_releases_the_accumulator_so_it_can_be_recorded_again()
    {
        var store = new FlakyUsageStore(failures: 1);
        var recorder = new UsageRecorder(store);
        var accumulator = Finished();

        await Assert.ThrowsAsync<IOException>(() =>
            recorder.RecordAsync(accumulator, null, TestContext.Current.CancellationToken).AsTask());
        await recorder.RecordAsync(accumulator, null, TestContext.Current.CancellationToken);

        Assert.Single(store.Inner.Records);
    }

    [Fact]
    public async Task A_cancelled_record_call_appends_nothing()
    {
        var store = new InMemoryUsageStore();
        var recorder = new UsageRecorder(store);
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            recorder.RecordAsync(Finished(), null, cts.Token).AsTask());

        Assert.Empty(store.Records);
    }

    /// <summary>A store whose first appends throw, to check that a failed append can be retried.</summary>
    private sealed class FlakyUsageStore(int failures) : IUsageStore
    {
        private int _remainingFailures = failures;

        public InMemoryUsageStore Inner { get; } = new();

        public ValueTask AppendAsync(GatewayUsageRecord record, CancellationToken ct)
        {
            if (_remainingFailures > 0)
            {
                _remainingFailures--;
                throw new IOException("disk unavailable");
            }

            return Inner.AppendAsync(record, ct);
        }
    }
}
