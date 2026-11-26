using Bazaar.Server.GcReactions;
using Bazaar.Server.GcReactions.Abstractions;
using Bazaar.Testing;
using Microsoft.Extensions.Configuration;

namespace Bazaar.Server.GcReactions.Tests;

public class EmojiRulesTests
{
    [Theory]
    [InlineData("👍")]
    [InlineData("❤️")]
    [InlineData("👍🏽")]
    [InlineData("👨‍👩‍👧")]
    [InlineData("1️⃣")]
    [InlineData("🇳🇬")]
    public void One_emoji_is_accepted(string emoji) => Assert.Null(EmojiRules.Refusal(emoji, 32));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("a")]
    [InlineData("ok")]
    [InlineData("1")]
    [InlineData("👍👍")]
    [InlineData("👍 ")]
    [InlineData(" ")]
    [InlineData("\n")]
    [InlineData("👍\n")]
    [InlineData("�")]
    public void Anything_that_is_not_exactly_one_emoji_is_refused(string? emoji) =>
        Assert.NotNull(EmojiRules.Refusal(emoji, 32));

    [Fact]
    public void The_byte_limit_is_the_operator_setting()
    {
        Assert.Null(EmojiRules.Refusal("👨‍👩‍👧", 32)); // 18 bytes
        Assert.NotNull(EmojiRules.Refusal("👨‍👩‍👧", 8));
    }
}

public class ReactionFoldTests
{
    private static readonly Guid _ada = Guid.Parse("00000000-0000-7000-8000-00000000000a");
    private static readonly Guid _bob = Guid.Parse("00000000-0000-7000-8000-00000000000b");

    private static ReactionEventRow Row(long seq, Guid by, long target, string emoji, bool add) => new(seq, by, target, emoji, add);

    [Fact]
    public void An_add_followed_by_a_remove_folds_to_nothing()
    {
        var groups = ReactionFold.Fold([Row(5, _ada, 2, "👍", true), Row(6, _ada, 2, "👍", false)]);

        Assert.Empty(groups);
    }

    [Fact]
    public void The_latest_event_per_person_message_and_emoji_wins()
    {
        var groups = ReactionFold.Fold([
            Row(5, _ada, 2, "👍", true),
            Row(6, _ada, 2, "👍", false),
            Row(7, _ada, 2, "👍", true),
            Row(8, _bob, 2, "👍", true),
        ]);

        var group = Assert.Single(groups);
        Assert.Equal((2L, "👍"), (group.TargetN, group.Emoji));
        Assert.Equal([_ada, _bob], group.By);
    }

    [Fact]
    public void Rows_are_applied_by_seq_whatever_their_order()
    {
        var groups = ReactionFold.Fold([Row(6, _ada, 2, "👍", false), Row(5, _ada, 2, "👍", true)]);

        Assert.Empty(groups);
    }

    [Fact]
    public void Groups_are_ordered_by_message_then_by_when_the_emoji_first_appeared()
    {
        var groups = ReactionFold.Fold([
            Row(10, _ada, 9, "🎉", true),
            Row(11, _ada, 3, "🔥", true),
            Row(12, _bob, 3, "👍", true),
            Row(13, _bob, 3, "🔥", true),
        ]);

        Assert.Equal([(3L, "🔥"), (3L, "👍"), (9L, "🎉")], groups.Select(g => (g.TargetN, g.Emoji)));
    }

    [Fact]
    public void A_remove_by_someone_who_never_added_changes_nothing()
    {
        var groups = ReactionFold.Fold([Row(5, _ada, 2, "👍", true), Row(6, _bob, 2, "👍", false)]);

        Assert.Equal([_ada], Assert.Single(groups).By);
    }

    [Fact]
    public void InEffectSeq_names_the_event_that_put_the_reaction_in_effect()
    {
        ReactionEventRow[] rows = [Row(5, _ada, 2, "👍", true), Row(6, _bob, 2, "👍", true), Row(7, _ada, 3, "👍", false)];

        Assert.Equal(5, ReactionFold.InEffectSeq(rows, _ada, 2, "👍"));
        Assert.Null(ReactionFold.InEffectSeq(rows, _ada, 3, "👍"));
        Assert.Null(ReactionFold.InEffectSeq(rows, _ada, 2, "🎉"));
    }
}

public class SlidingRateLimiterTests
{
    private static readonly Guid _gc = Guid.NewGuid();
    private static readonly Guid _ada = Guid.NewGuid();

    [Fact]
    public void The_limit_applies_per_person_and_per_gc_and_frees_up_with_time()
    {
        var clock = new FakeClock();
        var limiter = new SlidingRateLimiter(clock, TimeSpan.FromMinutes(1));

        Assert.True(limiter.TryAcquire(_gc, _ada, 2, out _));
        clock.Advance(TimeSpan.FromSeconds(10));
        Assert.True(limiter.TryAcquire(_gc, _ada, 2, out _));
        Assert.False(limiter.TryAcquire(_gc, _ada, 2, out var wait));
        Assert.Equal(TimeSpan.FromSeconds(50), wait);

        Assert.True(limiter.TryAcquire(Guid.NewGuid(), _ada, 2, out _));
        Assert.True(limiter.TryAcquire(_gc, Guid.NewGuid(), 2, out _));

        clock.Advance(TimeSpan.FromSeconds(50));
        Assert.True(limiter.TryAcquire(_gc, _ada, 2, out _));
    }
}

public class TypingTrackerTests
{
    private static readonly Guid _gc = Guid.NewGuid();
    private static readonly Guid _ada = Guid.NewGuid();
    private static readonly TimeSpan _interval = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan _ttl = TimeSpan.FromSeconds(5);

    [Fact]
    public void A_refresh_inside_the_minimum_interval_is_dropped_and_one_after_it_is_taken()
    {
        var clock = new FakeClock();
        var tracker = new TypingTracker(clock);

        Assert.True(tracker.TryAccept(_gc, _ada, true, _interval, _ttl));
        clock.Advance(TimeSpan.FromSeconds(1));
        Assert.False(tracker.TryAccept(_gc, _ada, true, _interval, _ttl));
        clock.Advance(TimeSpan.FromSeconds(1));
        Assert.True(tracker.TryAccept(_gc, _ada, true, _interval, _ttl));
    }

    [Fact]
    public void A_stop_is_always_taken_and_lets_the_next_start_through()
    {
        var tracker = new TypingTracker(new FakeClock());

        Assert.True(tracker.TryAccept(_gc, _ada, true, _interval, _ttl));
        Assert.True(tracker.TryAccept(_gc, _ada, false, _interval, _ttl));
        Assert.Equal(0, tracker.Count);
        Assert.True(tracker.TryAccept(_gc, _ada, true, _interval, _ttl));
    }
}

public class GcReactionsOptionsTests
{
    private static IConfigurationSection Section(params (string Key, string Value)[] values) =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(values.ToDictionary(v => $"{GcReactionsOptions.SectionName}:{v.Key}", v => (string?)v.Value))
            .Build()
            .GetSection(GcReactionsOptions.SectionName);

    [Fact]
    public void Defaults_match_the_docs()
    {
        var o = GcReactionsOptionsReader.Read(Section());

        Assert.Equal((true, 32, 20, 10, 30), (o.ReactionsEnabled, o.EmojiMaxBytes, o.MaxDistinctPerMessage, o.MaxPerPersonPerMessage, o.RatePerMinute));
        Assert.Equal((true, 5, 2), (o.TypingEnabled, o.TypingTtlS, o.TypingMinIntervalS));
    }

    [Fact]
    public void Operator_values_override_the_defaults()
    {
        var o = GcReactionsOptionsReader.Read(Section(("ReactionsEnabled", "false"), ("RatePerMinute", "5"), ("TypingTtlS", "8"), ("TypingMinIntervalS", "3")));

        Assert.False(o.ReactionsEnabled);
        Assert.Equal((5, 8, 3), (o.RatePerMinute, o.TypingTtlS, o.TypingMinIntervalS));
    }

    [Theory]
    [InlineData("RatePerMinute", "0")]
    [InlineData("RatePerMinute", "100000")]
    [InlineData("TypingTtlS", "1")]
    [InlineData("TypingTtlS", "60")]
    [InlineData("TypingMinIntervalS", "9")]
    [InlineData("EmojiMaxBytes", "1")]
    [InlineData("MaxDistinctPerMessage", "abc")]
    [InlineData("TypingEnabled", "maybe")]
    public void A_value_that_does_not_parse_or_is_out_of_range_fails_at_start(string key, string value) =>
        Assert.Throws<InvalidOperationException>(() => GcReactionsOptionsReader.Read(Section((key, value))));
}
