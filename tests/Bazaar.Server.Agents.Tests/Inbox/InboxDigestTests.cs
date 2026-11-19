using Bazaar.Server.Agents.Inbox;

namespace Bazaar.Server.Tests.Agents.Inbox;

public class InboxDigestTests
{
    private static readonly DateTimeOffset _now = new(2026, 10, 26, 12, 0, 0, TimeSpan.Zero);
    private static readonly Guid _agent = Guid.Parse("00000000-0000-0000-0000-00000000000a");
    private static readonly Guid _gcA = Guid.Parse("00000000-0000-0000-0000-0000000000c1");
    private static readonly Guid _gcB = Guid.Parse("00000000-0000-0000-0000-0000000000c2");

    private static InboxCandidate Digest(Guid gc, long? seq) =>
        new(Guid.NewGuid(), _agent, InboxKind.GcDigest, gc, seq, _now);

    [Fact]
    public void Batch_gives_min_max_and_count_per_gc()
    {
        var ranges = DigestBatcher.Batch([Digest(_gcA, 5), Digest(_gcA, 2), Digest(_gcA, 9), Digest(_gcB, 4)]);

        Assert.Equal([new DigestRange(_gcA, 2, 9, 3), new DigestRange(_gcB, 4, 4, 1)], ranges);
    }

    [Fact]
    public void Batch_counts_items_without_a_sequence_as_zero_and_ignores_other_kinds()
    {
        var unsequenced = Digest(_gcA, null);
        var mention = new InboxCandidate(Guid.NewGuid(), _agent, InboxKind.Mention, _gcA, 50, _now);

        var ranges = DigestBatcher.Batch([unsequenced, Digest(_gcA, 6), mention]);

        Assert.Equal([new DigestRange(_gcA, 0, 6, 2)], ranges);
    }

    [Fact]
    public void IsDue_is_true_with_no_previous_digest()
    {
        Assert.True(DigestSchedule.IsDue(null, _now, TimeSpan.FromHours(2)));
    }

    [Fact]
    public void IsDue_depends_on_the_interval_elapsed()
    {
        var interval = TimeSpan.FromHours(2);

        Assert.False(DigestSchedule.IsDue(_now.AddHours(-1), _now, interval));
        Assert.True(DigestSchedule.IsDue(_now.AddHours(-2), _now, interval));
    }

    [Fact]
    public void IsDue_refuses_a_zero_or_negative_interval()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => DigestSchedule.IsDue(null, _now, TimeSpan.Zero));
        Assert.Throws<ArgumentOutOfRangeException>(() => DigestSchedule.IsDue(null, _now, TimeSpan.FromMinutes(-1)));
    }
}
