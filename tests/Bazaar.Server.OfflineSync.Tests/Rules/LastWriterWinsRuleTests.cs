using Bazaar.OfflineSync;
using Bazaar.Server.OfflineSync.Rules;

namespace Bazaar.Server.OfflineSync.Tests.Rules;

public sealed class LastWriterWinsRuleTests
{
    private static OfflineWrite Position(string node, double x, Hlc hlc, Guid? writeId = null) =>
        RuleTestKit.Write(OfflineDataClass.ConstellationPosition, node, RuleTestKit.Json(new ConstellationPositionPayload(x, 0)), writeId: writeId, hlc: hlc);

    private static OfflineWrite Pref(string key, string value, Hlc hlc, string? baseVersion = null, Guid? writeId = null) =>
        RuleTestKit.Write(OfflineDataClass.Preference, key, RuleTestKit.Json(new PreferencePayload(value)), baseVersion, writeId, hlc);

    [Fact]
    public async Task Later_hlc_wins_and_the_replaced_value_moves_to_history()
    {
        var state = new InMemoryLastWriterWinsState();
        var rule = new ConstellationPositionRule(state);
        var first = Position("node-1", 1, RuleTestKit.At(1000));
        var second = Position("node-1", 2, RuleTestKit.At(2000));

        await rule.MergeAsync(first, CancellationToken.None);
        var outcome = await rule.MergeAsync(second, CancellationToken.None);

        Assert.Equal(OfflineOutcomeKind.Applied, outcome.Kind);
        Assert.Equal(second.WriteId, (await state.GetCurrentAsync("node-1", CancellationToken.None))?.WriteId);
        Assert.Equal(first.WriteId, Assert.Single(state.GetHistory("node-1")).WriteId);
    }

    [Fact]
    public async Task Earlier_hlc_arriving_later_loses_and_is_kept_in_history()
    {
        var state = new InMemoryLastWriterWinsState();
        var rule = new ConstellationPositionRule(state);
        var newer = Position("node-1", 2, RuleTestKit.At(2000));
        var older = Position("node-1", 1, RuleTestKit.At(1000));
        await rule.MergeAsync(newer, CancellationToken.None);

        var outcome = await rule.MergeAsync(older, CancellationToken.None);

        Assert.Equal(OfflineOutcomeKind.Applied, outcome.Kind);
        Assert.Equal(LastWriterWinsRule.FormatVersion(RuleTestKit.At(2000)), outcome.ServerVersion);
        Assert.Equal(newer.WriteId, (await state.GetCurrentAsync("node-1", CancellationToken.None))?.WriteId);
        Assert.Equal(older.WriteId, Assert.Single(state.GetHistory("node-1")).WriteId);
    }

    [Fact]
    public async Task Malformed_position_is_rejected()
    {
        var state = new InMemoryLastWriterWinsState();
        var rule = new ConstellationPositionRule(state);
        var bad = RuleTestKit.Write(OfflineDataClass.ConstellationPosition, "node-1", "[1,2]"u8.ToArray(), hlc: RuleTestKit.At(1000));

        var outcome = await rule.MergeAsync(bad, CancellationToken.None);

        Assert.Equal(OfflineOutcomeKind.Rejected, outcome.Kind);
        Assert.Null(await state.GetCurrentAsync("node-1", CancellationToken.None));
    }

    [Fact]
    public async Task Replay_is_a_duplicate_even_when_the_write_lost()
    {
        var state = new InMemoryLastWriterWinsState();
        var rule = new ConstellationPositionRule(state);
        var older = Position("node-1", 1, RuleTestKit.At(1000));
        await rule.MergeAsync(Position("node-1", 2, RuleTestKit.At(2000)), CancellationToken.None);
        await rule.MergeAsync(older, CancellationToken.None);

        var replay = await rule.MergeAsync(older, CancellationToken.None);

        Assert.Equal(OfflineOutcomeKind.Duplicate, replay.Kind);
        Assert.Single(state.GetHistory("node-1"));
    }

    [Fact]
    public async Task Preference_without_safety_relevance_is_last_writer_wins_even_on_conflict()
    {
        var state = new InMemoryLastWriterWinsState();
        var review = new RecordingReviewSink();
        var rule = new PreferenceRule(state, new FixedSafetyPolicy("notify.mentions"), review);
        await rule.MergeAsync(Pref("theme", "dark", RuleTestKit.At(1000)), CancellationToken.None);

        var conflicting = Pref("theme", "light", RuleTestKit.At(2000, device: RuleTestKit.DeviceB), baseVersion: "unrelated");
        var outcome = await rule.MergeAsync(conflicting, CancellationToken.None);

        Assert.Equal(OfflineOutcomeKind.Applied, outcome.Kind);
        Assert.Equal(conflicting.WriteId, (await state.GetCurrentAsync("theme", CancellationToken.None))?.WriteId);
        Assert.Empty(review.Items);
    }

    [Fact]
    public async Task Conflicting_safety_preference_is_held_for_review()
    {
        var state = new InMemoryLastWriterWinsState();
        var review = new RecordingReviewSink();
        var rule = new PreferenceRule(state, new FixedSafetyPolicy("notify.mentions"), review);
        var original = Pref("notify.mentions", "all", RuleTestKit.At(1000));
        await rule.MergeAsync(original, CancellationToken.None);

        var conflicting = Pref("notify.mentions", "none", RuleTestKit.At(2000, device: RuleTestKit.DeviceB), baseVersion: "stale");
        var outcome = await rule.MergeAsync(conflicting, CancellationToken.None);

        Assert.Equal(OfflineOutcomeKind.HeldForReview, outcome.Kind);
        Assert.Equal(original.WriteId, (await state.GetCurrentAsync("notify.mentions", CancellationToken.None))?.WriteId);
        Assert.Equal("preference-held", Assert.Single(review.Items).Kind);
    }

    [Fact]
    public async Task Safety_preference_written_on_the_current_base_applies()
    {
        var state = new InMemoryLastWriterWinsState();
        var rule = new PreferenceRule(state, new FixedSafetyPolicy("notify.mentions"), new RecordingReviewSink());
        await rule.MergeAsync(Pref("notify.mentions", "all", RuleTestKit.At(1000)), CancellationToken.None);
        var current = LastWriterWinsRule.FormatVersion(RuleTestKit.At(1000));

        var outcome = await rule.MergeAsync(Pref("notify.mentions", "none", RuleTestKit.At(2000), baseVersion: current), CancellationToken.None);

        Assert.Equal(OfflineOutcomeKind.Applied, outcome.Kind);
    }

    [Fact]
    public async Task Safety_preference_with_the_same_value_on_a_stale_base_applies()
    {
        var state = new InMemoryLastWriterWinsState();
        var rule = new PreferenceRule(state, new FixedSafetyPolicy("notify.mentions"), new RecordingReviewSink());
        await rule.MergeAsync(Pref("notify.mentions", "all", RuleTestKit.At(1000)), CancellationToken.None);

        var outcome = await rule.MergeAsync(Pref("notify.mentions", "all", RuleTestKit.At(2000), baseVersion: "stale"), CancellationToken.None);

        Assert.Equal(OfflineOutcomeKind.Applied, outcome.Kind);
    }
}
