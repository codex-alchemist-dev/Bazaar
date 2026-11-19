using Bazaar.Server.Agents.Inbox;

namespace Bazaar.Server.Tests.Agents.Inbox;

public class InboxSelectorTests
{
    private static readonly DateTimeOffset _now = new(2026, 10, 26, 12, 0, 0, TimeSpan.Zero);
    private static readonly Guid _agent = Guid.Parse("00000000-0000-0000-0000-00000000000a");
    private static readonly Guid _gc = Guid.Parse("00000000-0000-0000-0000-0000000000c1");

    private static InboxCandidate Item(string id, string kind, long? gcSeq = null, DateTimeOffset? dueAt = null) =>
        new(Guid.Parse(id), _agent, kind, _gc, gcSeq, dueAt ?? _now.AddMinutes(-1));

    [Fact]
    public void Select_excludes_items_due_after_now()
    {
        var future = Item("00000000-0000-0000-0000-000000000001", InboxKind.Interrupt, dueAt: _now.AddSeconds(1));
        var due = Item("00000000-0000-0000-0000-000000000002", InboxKind.Mention);

        var selected = InboxSelector.Select([future, due], _now);

        Assert.Equal([due], selected);
    }

    [Fact]
    public void Select_orders_by_rank_then_due_time_then_gc_seq_nulls_last_then_id()
    {
        var olderDue = _now.AddMinutes(-10);
        var mentionLaterSeq = Item("00000000-0000-0000-0000-000000000010", InboxKind.Mention, gcSeq: 9, dueAt: olderDue);
        var mentionEarlierSeq = Item("00000000-0000-0000-0000-000000000011", InboxKind.Mention, gcSeq: 3, dueAt: olderDue);
        var mentionNullSeq = Item("00000000-0000-0000-0000-000000000012", InboxKind.Mention, gcSeq: null, dueAt: olderDue);
        var mentionLaterDue = Item("00000000-0000-0000-0000-000000000013", InboxKind.Mention, gcSeq: 1, dueAt: _now.AddMinutes(-1));
        var interrupt = Item("00000000-0000-0000-0000-000000000014", InboxKind.Interrupt, gcSeq: null, dueAt: _now.AddMinutes(-1));

        var selected = InboxSelector.Select([mentionNullSeq, mentionLaterDue, mentionLaterSeq, interrupt, mentionEarlierSeq], _now);

        Assert.Equal([interrupt, mentionEarlierSeq, mentionLaterSeq, mentionNullSeq, mentionLaterDue], selected);
    }

    [Fact]
    public void Select_breaks_full_ties_by_id()
    {
        var b = Item("00000000-0000-0000-0000-0000000000bb", InboxKind.Assignment, gcSeq: 5);
        var a = Item("00000000-0000-0000-0000-0000000000aa", InboxKind.Assignment, gcSeq: 5);

        var selected = InboxSelector.Select([b, a], _now);

        Assert.Equal([a, b], selected);
    }

    [Fact]
    public void Plan_returns_the_interrupt_as_primary_and_leaves_the_mention_out_of_the_batch()
    {
        var interrupt = Item("00000000-0000-0000-0000-000000000020", InboxKind.Interrupt);
        var mention = Item("00000000-0000-0000-0000-000000000021", InboxKind.Mention, gcSeq: 4);

        var batch = InboxSelector.Plan([mention, interrupt], _now);

        Assert.Equal(interrupt, batch.Primary);
        Assert.Empty(batch.Digest);
    }

    [Fact]
    public void Digest_only_plan_has_no_primary_and_grouped_ranges()
    {
        var otherGc = Guid.Parse("00000000-0000-0000-0000-0000000000c2");
        var digestA = Item("00000000-0000-0000-0000-000000000030", InboxKind.GcDigest, gcSeq: 7);
        var digestB = Item("00000000-0000-0000-0000-000000000031", InboxKind.GcDigest, gcSeq: 2);
        var digestOther = digestA with { Id = Guid.Parse("00000000-0000-0000-0000-000000000032"), GcId = otherGc, GcSeq = 11 };

        var batch = InboxSelector.Plan([digestA, digestOther, digestB], _now);

        Assert.Null(batch.Primary);
        Assert.Equal(
            [new DigestRange(_gc, 2, 7, 2), new DigestRange(otherGc, 11, 11, 1)],
            batch.Digest);
    }
}
