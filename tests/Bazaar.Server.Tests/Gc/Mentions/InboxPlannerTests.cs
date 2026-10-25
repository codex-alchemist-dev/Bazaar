using Bazaar.Server.Gc.Mentions;
using Bazaar.Server.Gc.Model;

namespace Bazaar.Server.Tests.Gc.Mentions;

public class InboxPlannerTests
{
    private static readonly Guid GcId = Id(100);
    private static readonly Guid Author = Id(2);
    private static readonly Guid Ana = Id(3);
    private static readonly Guid Bob = Id(4);

    private static readonly GcRecord Gc = new(GcId, null, "general", null);

    private static Guid Id(int n) => new(n, 0, 0, new byte[8]);

    [Fact]
    public void One_inbox_row_per_distinct_target_with_gc_and_seq()
    {
        var accepted = new[]
        {
            new AcceptedMention(Ana, 0, 4),
            new AcceptedMention(Bob, 5, 4),
        };

        var rows = InboxPlanner.PlanMentionInbox(Gc, 42, accepted, Author);

        Assert.Equal(
            [new InboxRow(Ana, GcId, 42, InboxReason.Mention), new InboxRow(Bob, GcId, 42, InboxReason.Mention)],
            rows);
    }

    [Fact]
    public void Author_self_mention_produces_no_inbox_row()
    {
        var accepted = new[] { new AcceptedMention(Author, 0, 4) };

        var rows = InboxPlanner.PlanMentionInbox(Gc, 7, accepted, Author);

        Assert.Empty(rows);
    }

    [Fact]
    public void Duplicate_mentions_of_one_person_produce_one_row()
    {
        var accepted = new[]
        {
            new AcceptedMention(Ana, 0, 4),
            new AcceptedMention(Ana, 10, 4),
            new AcceptedMention(Ana, 20, 4),
        };

        var rows = InboxPlanner.PlanMentionInbox(Gc, 9, accepted, Author);

        var row = Assert.Single(rows);
        Assert.Equal(new InboxRow(Ana, GcId, 9, InboxReason.Mention), row);
    }

    [Fact]
    public void Author_is_skipped_while_other_targets_still_get_rows()
    {
        var accepted = new[]
        {
            new AcceptedMention(Author, 0, 4),
            new AcceptedMention(Bob, 5, 4),
        };

        var rows = InboxPlanner.PlanMentionInbox(Gc, 11, accepted, Author);

        Assert.Equal(new InboxRow(Bob, GcId, 11, InboxReason.Mention), Assert.Single(rows));
    }

    [Fact]
    public void Inbox_rows_keep_first_mention_order()
    {
        var accepted = new[]
        {
            new AcceptedMention(Bob, 0, 4),
            new AcceptedMention(Ana, 5, 4),
            new AcceptedMention(Bob, 10, 4),
        };

        var rows = InboxPlanner.PlanMentionInbox(Gc, 3, accepted, Author);

        Assert.Equal([Bob, Ana], rows.Select(r => r.PrincipalId));
    }

    [Fact]
    public void Mention_rows_keep_self_mentions_and_collapse_duplicates()
    {
        var accepted = new[]
        {
            new AcceptedMention(Author, 0, 4),
            new AcceptedMention(Ana, 5, 4),
            new AcceptedMention(Ana, 10, 4),
        };

        var rows = InboxPlanner.PlanMentionRows(Gc, 15, accepted);

        Assert.Equal(
            [new GcMentionRow(GcId, 15, Author), new GcMentionRow(GcId, 15, Ana)],
            rows);
    }
}
