namespace Bazaar.Push.Tests;

/// <summary>Kind parsing and the bare-wake handler. Pure: fakes only, no socket, no notification service.</summary>
public sealed class BareWakeTests
{
    [Theory]
    [InlineData("mention", WakeKind.Mention)]
    [InlineData("dm", WakeKind.DirectMessage)]
    [InlineData("owner_decision", WakeKind.OwnerDecision)]
    [InlineData("quota", WakeKind.Quota)]
    public void Known_kind_tokens_map_to_their_kind(string raw, WakeKind expected)
    {
        Assert.Equal(expected, BareWakeParser.Parse(raw));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("Mention")]
    [InlineData("mention ")]
    [InlineData("mention:gc-7f3a")]
    [InlineData("gc_message")]
    [InlineData("owner decision")]
    public void Anything_else_is_unknown(string? raw)
    {
        Assert.Equal(WakeKind.Unknown, BareWakeParser.Parse(raw));
    }

    [Fact]
    public void Overlong_values_are_unknown()
    {
        var raw = new string('m', BareWakeParser.MaxKindLength + 1);

        Assert.Equal(WakeKind.Unknown, BareWakeParser.Parse(raw));
    }

    [Fact]
    public async Task Wake_with_new_events_fetches_once_and_notifies()
    {
        var fetcher = new FakeWakeFetcher(newEvents: 2);
        var notifier = new FakeLocalNotifier();
        var handler = new BareWakeHandler(fetcher, notifier);

        var outcome = await handler.HandleAsync("mention", CancellationToken.None);

        Assert.Equal(WakeOutcome.Notified, outcome);
        Assert.Equal([WakeKind.Mention], fetcher.Fetched);
        Assert.Equal([WakeKind.Mention], notifier.Shown);
    }

    [Fact]
    public async Task Late_or_repeated_wake_with_nothing_new_shows_no_notification()
    {
        var fetcher = new FakeWakeFetcher(newEvents: 0);
        var notifier = new FakeLocalNotifier();
        var handler = new BareWakeHandler(fetcher, notifier);

        var outcome = await handler.HandleAsync("dm", CancellationToken.None);

        Assert.Equal(WakeOutcome.NothingNew, outcome);
        Assert.Equal([WakeKind.DirectMessage], fetcher.Fetched);
        Assert.Empty(notifier.Shown);
    }

    [Fact]
    public async Task Unknown_kind_still_fetches_because_the_cursor_makes_the_fetch_idempotent()
    {
        var fetcher = new FakeWakeFetcher(newEvents: 1);
        var notifier = new FakeLocalNotifier();
        var handler = new BareWakeHandler(fetcher, notifier);

        var outcome = await handler.HandleAsync("something-new-from-a-newer-server", CancellationToken.None);

        Assert.Equal(WakeOutcome.Notified, outcome);
        Assert.Equal([WakeKind.Unknown], fetcher.Fetched);
    }

    [Fact]
    public async Task Missing_kind_fetches_as_unknown()
    {
        var fetcher = new FakeWakeFetcher(newEvents: 0);
        var handler = new BareWakeHandler(fetcher, new FakeLocalNotifier());

        await handler.HandleAsync(null, CancellationToken.None);

        Assert.Equal([WakeKind.Unknown], fetcher.Fetched);
    }

    [Fact]
    public async Task Failed_fetch_reports_failed_and_shows_nothing()
    {
        var fetcher = new FakeWakeFetcher(failure: new IOException("socket reset"));
        var notifier = new FakeLocalNotifier();
        var handler = new BareWakeHandler(fetcher, notifier);

        var outcome = await handler.HandleAsync("mention", CancellationToken.None);

        Assert.Equal(WakeOutcome.Failed, outcome);
        Assert.Empty(notifier.Shown);
    }

    [Fact]
    public async Task Cancellation_is_not_swallowed()
    {
        var fetcher = new FakeWakeFetcher(failure: new OperationCanceledException());
        var handler = new BareWakeHandler(fetcher, new FakeLocalNotifier());

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => handler.HandleAsync("mention", CancellationToken.None));
    }

    [Fact]
    public async Task Already_cancelled_token_stops_before_fetching()
    {
        var fetcher = new FakeWakeFetcher(newEvents: 1);
        var handler = new BareWakeHandler(fetcher, new FakeLocalNotifier());
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => handler.HandleAsync("mention", cts.Token));
        Assert.Empty(fetcher.Fetched);
    }

    [Fact]
    public void Constructor_rejects_missing_collaborators()
    {
        Assert.Throws<ArgumentNullException>(() => new BareWakeHandler(null!, new FakeLocalNotifier()));
        Assert.Throws<ArgumentNullException>(() => new BareWakeHandler(new FakeWakeFetcher(), null!));
    }
}
