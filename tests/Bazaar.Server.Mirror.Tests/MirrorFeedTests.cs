using Microsoft.Extensions.Time.Testing;

namespace Bazaar.Server.Mirror.Tests;

public class MirrorFeedTests
{
    private readonly InMemoryMirrorStore _store = new();

    private (MirrorFeed Feed, MirrorService Service) Make(MirrorOptions? options = null)
    {
        var time = new FakeTimeProvider();
        var service = new MirrorService(_store, options ?? new MirrorOptions { EvidenceThreshold = 2 }, time);
        return (new MirrorFeed(service, time), service);
    }

    [Fact]
    public async Task Card_decisions_feed_the_right_direction_and_replays_add_nothing()
    {
        var ct = TestContext.Current.CancellationToken;
        var (feed, service) = Make();

        Assert.True(await feed.CardDecidedAsync("card-1", false, "too long", ["style:long"], ct));
        Assert.False(await feed.CardDecidedAsync("card-1", false, "too long", ["style:long"], ct));
        Assert.True(await feed.CardDecidedAsync("card-2", false, null, ["style:long"], ct));

        var rule = Assert.Single((await service.GetProfileAsync(ct))!.Rules);
        Assert.Equal((MirrorDirection.Avoid, 2), (rule.Direction, rule.Count));
    }

    [Fact]
    public async Task Edits_count_against_removed_topics_and_for_added_ones()
    {
        var ct = TestContext.Current.CancellationToken;
        var (feed, service) = Make();

        await feed.OwnerEditedAsync("edit-1", ["tone:chatty"], ["tone:plain"], null, ct);
        await feed.OwnerEditedAsync("edit-2", ["tone:chatty"], ["tone:plain"], null, ct);

        var rules = (await service.GetProfileAsync(ct))!.Rules;
        Assert.Contains(rules, r => r.Topic == "tone:chatty" && r.Direction == MirrorDirection.Avoid);
        Assert.Contains(rules, r => r.Topic == "tone:plain" && r.Direction == MirrorDirection.Prefer);
    }

    [Fact]
    public async Task Council_tie_breaks_comments_and_statements_each_feed_their_kind()
    {
        var ct = TestContext.Current.CancellationToken;
        var (feed, _) = Make();

        await feed.CouncilTieBrokenAsync("vote-1", "scope:big-refactor", false, "too risky", ct);
        await feed.ReviewCommentedAsync("pr-9#c1", "tests:required", MirrorDirection.Prefer, "add a test", ct);
        await feed.PreferenceStatedAsync("stmt-1", "docs:first", MirrorDirection.Prefer, "docs before code", ct);

        var kinds = (await _store.ListSignalsAsync(ct)).Select(s => s.Kind).Order().ToArray();
        Assert.Equal([MirrorSignalKind.ReviewComment, MirrorSignalKind.CouncilTieBreak, MirrorSignalKind.PreferenceStatement], kinds);
    }

    [Fact]
    public async Task Disabled_kinds_and_paused_learning_learn_nothing()
    {
        var ct = TestContext.Current.CancellationToken;
        var (feed, service) = Make(new MirrorOptions { SignalKinds = new HashSet<MirrorSignalKind> { MirrorSignalKind.CardAccepted } });

        Assert.False(await feed.CardDecidedAsync("c", false, null, ["t"], ct));
        await service.SetPausedAsync(true, ct);
        Assert.False(await feed.CardDecidedAsync("c2", true, null, ["t"], ct));
        Assert.Empty(await _store.ListSignalsAsync(ct));
    }

    [Fact]
    public async Task Blank_topics_are_ignored_and_a_blank_source_is_refused()
    {
        var ct = TestContext.Current.CancellationToken;
        var (feed, _) = Make();

        Assert.False(await feed.CardDecidedAsync("c", true, null, [" ", ""], ct));
        await Assert.ThrowsAsync<ArgumentException>(async () => await feed.CardDecidedAsync(" ", true, null, ["t"], ct));
    }
}
