using Bazaar.OfflineSync;
using Bazaar.Server.OfflineSync.Rules;

namespace Bazaar.Server.OfflineSync.Tests.Rules;

public sealed class GcMessageEditRuleTests
{
    [Fact]
    public async Task First_edit_is_applied_and_becomes_current()
    {
        var state = new InMemoryGcEditState();
        var rule = new GcMessageEditRule(state);

        var outcome = await rule.MergeAsync(RuleTestKit.Write(OfflineDataClass.GcMessageEdit, "msg-1", RuleTestKit.Json(new GcMessagePayload("edit")), baseVersion: "1"), CancellationToken.None);

        Assert.Equal(OfflineOutcomeKind.Applied, outcome.Kind);
        var current = await state.GetCurrentAsync("msg-1", CancellationToken.None);
        Assert.Equal(outcome.ServerVersion, current?.Sequence.ToString(System.Globalization.CultureInfo.InvariantCulture));
    }

    [Fact]
    public async Task Two_edits_from_the_same_base_are_concurrent_and_both_survive()
    {
        var state = new InMemoryGcEditState();
        var rule = new GcMessageEditRule(state);
        var phoneEdit = RuleTestKit.Write(OfflineDataClass.GcMessageEdit, "msg-1", RuleTestKit.Json(new GcMessagePayload("phone")), baseVersion: "1");
        var laptopEdit = RuleTestKit.Write(OfflineDataClass.GcMessageEdit, "msg-1", RuleTestKit.Json(new GcMessagePayload("laptop")), baseVersion: "1");

        var first = await rule.MergeAsync(phoneEdit, CancellationToken.None);
        var second = await rule.MergeAsync(laptopEdit, CancellationToken.None);

        Assert.Equal(OfflineOutcomeKind.Applied, first.Kind);
        Assert.Equal(OfflineOutcomeKind.ConcurrentEdit, second.Kind);
        Assert.Equal(2, state.GetVersions("msg-1").Count);
        var current = await state.GetCurrentAsync("msg-1", CancellationToken.None);
        Assert.Equal(laptopEdit.WriteId, current?.WriteId);
    }

    [Fact]
    public async Task Edit_from_a_different_base_is_applied_not_concurrent()
    {
        var state = new InMemoryGcEditState();
        var rule = new GcMessageEditRule(state);
        await rule.MergeAsync(RuleTestKit.Write(OfflineDataClass.GcMessageEdit, "msg-1", RuleTestKit.Json(new GcMessagePayload("a")), baseVersion: "1"), CancellationToken.None);

        var next = await rule.MergeAsync(RuleTestKit.Write(OfflineDataClass.GcMessageEdit, "msg-1", RuleTestKit.Json(new GcMessagePayload("b")), baseVersion: "2"), CancellationToken.None);

        Assert.Equal(OfflineOutcomeKind.Applied, next.Kind);
    }

    [Fact]
    public async Task Replay_of_an_edit_is_a_duplicate_and_adds_no_version()
    {
        var state = new InMemoryGcEditState();
        var rule = new GcMessageEditRule(state);
        var edit = RuleTestKit.Write(OfflineDataClass.GcMessageEdit, "msg-1", RuleTestKit.Json(new GcMessagePayload("a")), baseVersion: "1");
        await rule.MergeAsync(edit, CancellationToken.None);

        var replay = await rule.MergeAsync(edit, CancellationToken.None);

        Assert.Equal(OfflineOutcomeKind.Duplicate, replay.Kind);
        Assert.Single(state.GetVersions("msg-1"));
    }
}
