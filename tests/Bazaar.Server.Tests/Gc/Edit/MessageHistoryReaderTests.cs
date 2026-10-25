using Bazaar.Contracts.Gc;
using Bazaar.Server.Gc.Edit;
using Bazaar.Server.Gc.Model;

namespace Bazaar.Server.Tests.Gc.Edit;

public class MessageHistoryReaderTests
{
    private static readonly Guid GcId = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid MessageId = Guid.Parse("22222222-2222-2222-2222-222222222222");
    private static readonly Guid OtherMessageId = Guid.Parse("55555555-5555-5555-5555-555555555555");
    private static readonly Guid OtherGcId = Guid.Parse("66666666-6666-6666-6666-666666666666");
    private static readonly Guid AuthorId = Guid.Parse("33333333-3333-3333-3333-333333333333");
    private static readonly DateTimeOffset T0 = new(2026, 10, 25, 9, 0, 0, TimeSpan.Zero);

    private static readonly GcViewer Reader = new(AuthorId, PrincipalKind.Human);
    private static readonly Func<GcViewer, bool> CanRead = _ => true;
    private static readonly Func<GcViewer, bool> CannotRead = _ => false;

    private static MessageVersionRecord Version(int version, long seq, string body = "text")
    {
        return new MessageVersionRecord(GcId, MessageId, version, body, AuthorId, seq, T0.AddMinutes(version));
    }

    private static MessageHistoryFault Broken(MessageHistoryResult result)
    {
        return Assert.IsType<MessageHistoryChainBroken>(result).Fault;
    }

    [Fact]
    public void Single_version_is_not_edited()
    {
        var result = MessageHistoryReader.GetHistory([Version(1, 10)], Reader, CanRead);

        var found = Assert.IsType<MessageHistoryFound>(result);
        Assert.Equal(MessageId, found.History.MessageId);
        Assert.Single(found.History.Versions);
        Assert.False(found.History.Edited);
    }

    [Fact]
    public void Three_versions_are_edited_and_returned_oldest_first()
    {
        // Supplied out of order on purpose: the reader must sort by version.
        var versions = new[]
        {
            Version(3, 30, "third"),
            Version(1, 10, "first"),
            Version(2, 20, "second"),
        };

        var result = MessageHistoryReader.GetHistory(versions, Reader, CanRead);

        var history = Assert.IsType<MessageHistoryFound>(result).History;
        Assert.True(history.Edited);
        Assert.Equal(3, history.Versions.Count);
        Assert.Equal(1, history.Versions[0].Version);
        Assert.Equal(2, history.Versions[1].Version);
        Assert.Equal(3, history.Versions[2].Version);
        Assert.Equal("first", history.Versions[0].Body);
        Assert.Equal("second", history.Versions[1].Body);
        Assert.Equal("third", history.Versions[2].Body);
    }

    [Fact]
    public void Denied_viewer_gets_no_versions_and_the_check_sees_the_viewer()
    {
        GcViewer? seen = null;
        bool Check(GcViewer viewer)
        {
            seen = viewer;
            return false;
        }

        var result = MessageHistoryReader.GetHistory([Version(1, 10)], Reader, Check);

        Assert.IsType<MessageHistoryDenied>(result);
        Assert.Equal(Reader, seen);
    }

    [Fact]
    public void Denied_check_runs_before_the_chain_is_validated()
    {
        // A broken chain is still reported as denied, so the viewer learns nothing about it.
        var result = MessageHistoryReader.GetHistory([Version(1, 10), Version(3, 30)], Reader, CannotRead);

        Assert.IsType<MessageHistoryDenied>(result);
    }

    [Fact]
    public void Empty_list_is_a_broken_chain()
    {
        var result = MessageHistoryReader.GetHistory([], Reader, CanRead);

        Assert.Equal(MessageHistoryFault.Empty, Broken(result));
    }

    [Fact]
    public void Gap_in_version_numbers_is_a_broken_chain_with_no_partial_list()
    {
        var result = MessageHistoryReader.GetHistory([Version(1, 10), Version(3, 30)], Reader, CanRead);

        Assert.IsNotType<MessageHistoryFound>(result);
        Assert.Equal(MessageHistoryFault.VersionGap, Broken(result));
    }

    [Fact]
    public void Chain_not_starting_at_one_is_a_broken_chain()
    {
        var result = MessageHistoryReader.GetHistory([Version(2, 20), Version(3, 30)], Reader, CanRead);

        Assert.Equal(MessageHistoryFault.VersionGap, Broken(result));
    }

    [Fact]
    public void Duplicate_version_number_is_a_broken_chain()
    {
        var result = MessageHistoryReader.GetHistory(
            [Version(1, 10), Version(2, 20), Version(2, 30)],
            Reader,
            CanRead);

        Assert.Equal(MessageHistoryFault.DuplicateVersion, Broken(result));
    }

    [Fact]
    public void Versions_of_two_messages_are_a_broken_chain()
    {
        var other = Version(2, 20) with { MessageId = OtherMessageId };

        var result = MessageHistoryReader.GetHistory([Version(1, 10), other], Reader, CanRead);

        Assert.Equal(MessageHistoryFault.MixedMessages, Broken(result));
    }

    [Fact]
    public void Versions_from_two_gcs_are_a_broken_chain()
    {
        var other = Version(2, 20) with { GcId = OtherGcId };

        var result = MessageHistoryReader.GetHistory([Version(1, 10), other], Reader, CanRead);

        Assert.Equal(MessageHistoryFault.MixedGcs, Broken(result));
    }

    [Fact]
    public void Seq_that_does_not_increase_with_version_is_a_broken_chain()
    {
        var result = MessageHistoryReader.GetHistory([Version(1, 20), Version(2, 10)], Reader, CanRead);

        Assert.Equal(MessageHistoryFault.SeqNotIncreasing, Broken(result));
    }

    [Fact]
    public void Equal_seq_on_two_versions_is_a_broken_chain()
    {
        var result = MessageHistoryReader.GetHistory([Version(1, 10), Version(2, 10)], Reader, CanRead);

        Assert.Equal(MessageHistoryFault.SeqNotIncreasing, Broken(result));
    }
}
