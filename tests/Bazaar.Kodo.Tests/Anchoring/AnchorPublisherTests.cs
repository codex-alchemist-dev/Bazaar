using Bazaar.Kodo.Anchoring;
using Microsoft.Extensions.Time.Testing;

namespace Bazaar.Kodo.Tests.Anchoring;

public sealed class AnchorPublisherTests
{
    private static readonly AnchorRecord _anchor = new()
    {
        Version = 1,
        SinkId = "x",
        TreeSize = 1,
        Root = new byte[32],
        JournalHead = new byte[32],
        AnchoredAt = new DateTimeOffset(2026, 11, 19, 8, 0, 0, TimeSpan.Zero),
    };

    [Fact]
    public async Task OneBadSinkNeverStopsTheOthersOrThrows()
    {
        FakeTimeProvider time = new();
        AnchorPublisher publisher = new(TimeSpan.FromSeconds(5), time);
        StubSink[] sinks =
        [
            new("throws", _ => throw new InvalidOperationException("boom")),
            new("declines", _ => new ValueTask<bool>(false)),
            new("ok", _ => new ValueTask<bool>(true)),
        ];

        AnchorPublishReport report = await publisher.PublishAsync(_anchor, sinks, TestContext.Current.CancellationToken);

        Assert.Equal(
            [AnchorSinkOutcome.Failed, AnchorSinkOutcome.Rejected, AnchorSinkOutcome.Accepted],
            report.Results.Select(r => r.Outcome));
        Assert.True(report.AnyAccepted);
    }

    [Fact]
    public async Task HangingSinkTimesOutOnTheInjectedClock()
    {
        FakeTimeProvider time = new();
        AnchorPublisher publisher = new(TimeSpan.FromSeconds(5), time);
        StubSink hang = new("hang", async token =>
        {
            await Task.Delay(Timeout.Infinite, token);
            return true;
        });

        Task<AnchorPublishReport> pending = publisher.PublishAsync(_anchor, [hang], TestContext.Current.CancellationToken);
        time.Advance(TimeSpan.FromSeconds(5));
        AnchorPublishReport report = await pending;

        Assert.Equal(AnchorSinkOutcome.TimedOut, Assert.Single(report.Results).Outcome);
        Assert.False(report.AnyAccepted);
    }

    [Fact]
    public async Task NoSinksMeansNotAnchoredNotAnError()
    {
        AnchorPublishReport report = await new AnchorPublisher().PublishAsync(_anchor, [], TestContext.Current.CancellationToken);

        Assert.Empty(report.Results);
        Assert.False(report.AnyAccepted);
    }

    [Fact]
    public async Task CallerCancellationStillPropagates()
    {
        using CancellationTokenSource cts = new();
        await cts.CancelAsync();
        AnchorPublisher publisher = new();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => publisher.PublishAsync(_anchor, [new StubSink("a", _ => new ValueTask<bool>(true))], cts.Token));
    }

    [Fact]
    public void NonPositiveTimeoutIsRejected()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new AnchorPublisher(TimeSpan.Zero));
    }

    private sealed class StubSink(string id, Func<CancellationToken, ValueTask<bool>> publish) : IAnchorSink
    {
        public string Id => id;

        public ValueTask<bool> PublishAsync(AnchorRecord anchor, CancellationToken cancellationToken) => publish(cancellationToken);
    }
}
