using Bazaar.Server.Agents.Inbox;

namespace Bazaar.Server.Tests.Agents.Inbox;

public class InboxPriorityTests
{
    [Theory]
    [InlineData(InboxKind.Interrupt, 0)]
    [InlineData(InboxKind.Mention, 1)]
    [InlineData(InboxKind.Assignment, 2)]
    [InlineData(InboxKind.VoteRequest, 3)]
    [InlineData(InboxKind.Scheduled, 4)]
    [InlineData(InboxKind.Brainstorm, 5)]
    [InlineData(InboxKind.SkimmerPromoted, 6)]
    [InlineData(InboxKind.GcDigest, 7)]
    [InlineData(InboxKind.Constitution, 8)]
    public void Rank_follows_the_doc_06_order(string kind, int rank)
    {
        Assert.Equal(rank, InboxPriority.Rank(kind));
    }

    [Fact]
    public void Unknown_kind_is_refused()
    {
        var ex = Assert.Throws<ArgumentException>(() => InboxPriority.Rank("nudge"));

        Assert.Equal("kind", ex.ParamName);
    }

    [Fact]
    public void IsKnown_is_true_for_every_kind_and_false_otherwise()
    {
        Assert.All(InboxKind.All, kind => Assert.True(InboxPriority.IsKnown(kind)));
        Assert.False(InboxPriority.IsKnown("nudge"));
        Assert.Equal(9, InboxKind.All.Count);
    }
}
