using Bazaar.OfflineSync;
using Bazaar.Server.OfflineSync.Rules;

namespace Bazaar.Server.OfflineSync.Tests.Rules;

public sealed class GcMessageRuleTests
{
    [Fact]
    public async Task Appends_get_gapless_sequence_per_gc()
    {
        var state = new InMemoryGcAppendState();
        var rule = new GcMessageRule(state);

        var first = await rule.MergeAsync(RuleTestKit.Write(OfflineDataClass.GcMessage, "gc-1", RuleTestKit.Json(new GcMessagePayload("a"))), CancellationToken.None);
        var second = await rule.MergeAsync(RuleTestKit.Write(OfflineDataClass.GcMessage, "gc-1", RuleTestKit.Json(new GcMessagePayload("b"))), CancellationToken.None);
        var other = await rule.MergeAsync(RuleTestKit.Write(OfflineDataClass.GcMessage, "gc-2", RuleTestKit.Json(new GcMessagePayload("c"))), CancellationToken.None);

        Assert.Equal(OfflineOutcomeKind.Applied, first.Kind);
        Assert.Equal("1", first.ServerVersion);
        Assert.Equal("2", second.ServerVersion);
        Assert.Equal("1", other.ServerVersion);
        Assert.Equal(2, state.GetLog("gc-1").Count);
    }

    [Fact]
    public async Task Replay_of_the_same_write_id_is_a_duplicate_and_appends_nothing()
    {
        var state = new InMemoryGcAppendState();
        var rule = new GcMessageRule(state);
        var writeId = Guid.NewGuid();
        var write = RuleTestKit.Write(OfflineDataClass.GcMessage, "gc-1", RuleTestKit.Json(new GcMessagePayload("a")), writeId: writeId);

        await rule.MergeAsync(write, CancellationToken.None);
        var replay = await rule.MergeAsync(write, CancellationToken.None);

        Assert.Equal(OfflineOutcomeKind.Duplicate, replay.Kind);
        Assert.Single(state.GetLog("gc-1"));
    }

    [Fact]
    public async Task Malformed_payload_is_rejected_and_does_not_claim_the_write_id()
    {
        var state = new InMemoryGcAppendState();
        var rule = new GcMessageRule(state);
        var writeId = Guid.NewGuid();

        var bad = await rule.MergeAsync(RuleTestKit.Write(OfflineDataClass.GcMessage, "gc-1", "not json"u8.ToArray(), writeId: writeId), CancellationToken.None);
        var good = await rule.MergeAsync(RuleTestKit.Write(OfflineDataClass.GcMessage, "gc-1", RuleTestKit.Json(new GcMessagePayload("a")), writeId: writeId), CancellationToken.None);

        Assert.Equal(OfflineOutcomeKind.Rejected, bad.Kind);
        Assert.Equal(OfflineOutcomeKind.Applied, good.Kind);
        Assert.Single(state.GetLog("gc-1"));
    }
}
