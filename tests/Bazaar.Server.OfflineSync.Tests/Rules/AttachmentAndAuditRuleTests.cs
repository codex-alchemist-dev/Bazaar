using Bazaar.OfflineSync;
using Bazaar.Server.OfflineSync.Rules;

namespace Bazaar.Server.OfflineSync.Tests.Rules;

public sealed class AttachmentRuleTests
{
    private static readonly string _hash = new('a', 64);

    [Fact]
    public async Task New_attachment_is_stored_and_applied_with_its_hash_as_version()
    {
        var state = new InMemoryAttachmentState();
        var rule = new AttachmentRule(state);

        var outcome = await rule.MergeAsync(RuleTestKit.Write(OfflineDataClass.Attachment, _hash, new byte[] { 1, 2, 3 }), CancellationToken.None);

        Assert.Equal(OfflineOutcomeKind.Applied, outcome.Kind);
        Assert.Equal(_hash, outcome.ServerVersion);
        Assert.Equal(1, state.Count);
    }

    [Fact]
    public async Task Identical_second_attachment_is_a_no_op_applied_not_an_error()
    {
        var state = new InMemoryAttachmentState();
        var rule = new AttachmentRule(state);
        await rule.MergeAsync(RuleTestKit.Write(OfflineDataClass.Attachment, _hash, new byte[] { 1 }), CancellationToken.None);

        var second = await rule.MergeAsync(RuleTestKit.Write(OfflineDataClass.Attachment, _hash, new byte[] { 1 }), CancellationToken.None);

        Assert.Equal(OfflineOutcomeKind.Applied, second.Kind);
        Assert.Equal(1, state.Count);
    }

    [Theory]
    [InlineData("")]
    [InlineData("abc")]
    [InlineData("ABCDEF0123456789ABCDEF0123456789ABCDEF0123456789ABCDEF0123456789")]
    [InlineData("g00000000000000000000000000000000000000000000000000000000000000g")]
    public async Task Target_that_is_not_a_lowercase_sha256_hex_digest_is_rejected(string target)
    {
        var state = new InMemoryAttachmentState();
        var rule = new AttachmentRule(state);

        var outcome = await rule.MergeAsync(RuleTestKit.Write(OfflineDataClass.Attachment, target, new byte[] { 1 }), CancellationToken.None);

        Assert.Equal(OfflineOutcomeKind.Rejected, outcome.Kind);
        Assert.Equal(0, state.Count);
    }
}

public sealed class AuditActionRuleTests
{
    [Fact]
    public async Task Actions_are_appended_in_arrival_order_with_positions_as_versions()
    {
        var sink = new InMemoryAuditActionSink();
        var rule = new AuditActionRule(sink);
        var first = RuleTestKit.Write(OfflineDataClass.AuditAction, "audit", new byte[] { 1 });
        var second = RuleTestKit.Write(OfflineDataClass.AuditAction, "audit", new byte[] { 2 });

        var a = await rule.MergeAsync(first, CancellationToken.None);
        var b = await rule.MergeAsync(second, CancellationToken.None);

        Assert.Equal("1", a.ServerVersion);
        Assert.Equal("2", b.ServerVersion);
        Assert.Equal([first.WriteId, second.WriteId], sink.WriteIdsInOrder);
    }

    [Fact]
    public async Task Replayed_action_is_a_duplicate_and_is_not_appended_twice()
    {
        var sink = new InMemoryAuditActionSink();
        var rule = new AuditActionRule(sink);
        var action = RuleTestKit.Write(OfflineDataClass.AuditAction, "audit", new byte[] { 1 });
        await rule.MergeAsync(action, CancellationToken.None);

        var replay = await rule.MergeAsync(action, CancellationToken.None);

        Assert.Equal(OfflineOutcomeKind.Duplicate, replay.Kind);
        Assert.Single(sink.WriteIdsInOrder);
    }
}
