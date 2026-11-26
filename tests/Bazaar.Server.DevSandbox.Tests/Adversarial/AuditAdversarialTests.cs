namespace Bazaar.Server.DevSandbox.Tests.Adversarial;

public class AuditAdversarialTests
{
    private static readonly DateTimeOffset _t = new(2026, 11, 19, 0, 0, 0, TimeSpan.Zero);

    private static HashChainedAuditTrail Trail(int n = 4)
    {
        var trail = new HashChainedAuditTrail(new FixedTime(_t));
        for (var i = 0; i < n; i++)
        {
            trail.Append(new SandboxAction(SandboxActionKind.FileAccess, "fs.read", "/data/f" + i, "p" + i), SandboxDecision.Allowed, "granted");
        }

        return trail;
    }

    [Fact]
    public void A_clean_chain_with_its_stored_head_verifies()
    {
        var trail = Trail();

        Assert.True(HashChainedAuditTrail.VerifyEvents(trail.Events, trail.HeadHash));
    }

    [Fact]
    public void Tampering_with_one_event_without_rehashing_is_detected()
    {
        var trail = Trail();
        var events = trail.Events.ToArray();
        events[1] = events[1] with { Decision = SandboxDecision.Denied };

        Assert.False(HashChainedAuditTrail.VerifyEvents(events, trail.HeadHash));
    }

    [Fact]
    public void Tampering_with_one_event_and_rehashing_only_it_breaks_the_next_link()
    {
        var trail = Trail();
        var events = trail.Events.ToArray();
        var forged = events[1] with { Reason = "forged" };
        events[1] = forged with { Hash = HashChainedAuditTrail.ComputeHash(forged.Sequence, forged.At, forged.Action, forged.Decision, forged.Reason, forged.PreviousHash) };

        Assert.False(HashChainedAuditTrail.VerifyEvents(events));
    }

    [Fact]
    public void Rewriting_the_whole_tail_after_a_change_verifies_alone_but_not_against_the_stored_head()
    {
        var original = Trail();
        var forged = new HashChainedAuditTrail(new FixedTime(_t));
        forged.Append(new SandboxAction(SandboxActionKind.FileAccess, "fs.read", "/data/f0", "p0"), SandboxDecision.Allowed, "granted");
        forged.Append(new SandboxAction(SandboxActionKind.FileAccess, "fs.read", "/etc/passwd", ""), SandboxDecision.Allowed, "granted");

        Assert.True(HashChainedAuditTrail.VerifyEvents(forged.Events));
        Assert.False(HashChainedAuditTrail.VerifyEvents(forged.Events, original.HeadHash));
    }

    [Fact]
    public void Reordering_two_events_is_detected()
    {
        var trail = Trail();
        var events = trail.Events.ToArray();
        (events[1], events[2]) = (events[2], events[1]);

        Assert.False(HashChainedAuditTrail.VerifyEvents(events, trail.HeadHash));
    }

    [Fact]
    public void Dropping_the_last_event_is_caught_only_with_the_stored_head()
    {
        var trail = Trail();
        var truncated = trail.Events.Take(3).ToArray();

        Assert.True(HashChainedAuditTrail.VerifyEvents(truncated));
        Assert.False(HashChainedAuditTrail.VerifyEvents(truncated, trail.HeadHash));
    }

    [Fact]
    public void Dropping_the_first_event_is_detected_even_without_the_head()
    {
        var trail = Trail();
        var dropped = trail.Events.Skip(1).ToArray();

        Assert.False(HashChainedAuditTrail.VerifyEvents(dropped));
        Assert.False(HashChainedAuditTrail.VerifyEvents(dropped, trail.HeadHash));
    }

    [Fact]
    public void Replacing_the_chain_with_a_shorter_valid_one_is_caught_only_with_the_stored_head()
    {
        var trail = Trail(4);
        var shorter = Trail(2);

        Assert.True(HashChainedAuditTrail.VerifyEvents(shorter.Events));
        Assert.False(HashChainedAuditTrail.VerifyEvents(shorter.Events, trail.HeadHash));
    }

    [Fact]
    public void Emptying_the_chain_is_caught_only_with_the_stored_head()
    {
        var trail = Trail();

        Assert.True(HashChainedAuditTrail.VerifyEvents([]));
        Assert.False(HashChainedAuditTrail.VerifyEvents([], trail.HeadHash));
    }

    [Fact]
    public void Appending_a_forged_extra_event_with_a_valid_link_is_caught_by_the_stored_head()
    {
        var trail = Trail();
        var extra = Trail(5).Events;

        Assert.True(HashChainedAuditTrail.VerifyEvents(extra));
        Assert.False(HashChainedAuditTrail.VerifyEvents(extra, trail.HeadHash));
    }

    [Theory]
    [InlineData("a", "bc", "d", "e", "a", "b", "cd", "e")]
    [InlineData("fs.read", "/a", "", "granted", "fs.read/", "a", "", "granted")]
    [InlineData("", "ab", "c", "r", "a", "b", "c", "r")]
    [InlineData("x", "y", "z", "", "x", "y", "", "z")]
    [InlineData("a\0b", "c", "d", "e", "a", "b\0c", "d", "e")]
    public void Different_field_splits_that_concatenate_to_the_same_text_hash_differently(
        string c1, string t1, string p1, string r1, string c2, string t2, string p2, string r2)
    {
        var h1 = HashChainedAuditTrail.ComputeHash(0, _t, new SandboxAction(SandboxActionKind.FileAccess, c1, t1, p1), SandboxDecision.Allowed, r1, new string('0', 64));
        var h2 = HashChainedAuditTrail.ComputeHash(0, _t, new SandboxAction(SandboxActionKind.FileAccess, c2, t2, p2), SandboxDecision.Allowed, r2, new string('0', 64));

        Assert.NotEqual(h1, h2);
    }

    [Fact]
    public void Kind_and_decision_are_part_of_the_hash()
    {
        var a = new SandboxAction(SandboxActionKind.FileAccess, "c", "t");
        var z = new string('0', 64);

        Assert.NotEqual(
            HashChainedAuditTrail.ComputeHash(0, _t, a, SandboxDecision.Allowed, "r", z),
            HashChainedAuditTrail.ComputeHash(0, _t, a, SandboxDecision.Denied, "r", z));
        Assert.NotEqual(
            HashChainedAuditTrail.ComputeHash(0, _t, a, SandboxDecision.Allowed, "r", z),
            HashChainedAuditTrail.ComputeHash(0, _t, a with { Kind = SandboxActionKind.HostCall }, SandboxDecision.Allowed, "r", z));
    }

    [Fact]
    public void A_null_event_or_field_is_a_failed_verification_not_an_exception()
    {
        var trail = Trail(2);
        var events = trail.Events.ToArray();

        Assert.False(HashChainedAuditTrail.VerifyEvents([events[0], null!]));
        Assert.False(HashChainedAuditTrail.VerifyEvents([events[0], events[1] with { Action = null! }]));
    }

    [Fact]
    public void An_uppercased_hash_does_not_verify()
    {
        var trail = Trail(1);
        var events = trail.Events.ToArray();
        events[0] = events[0] with { Hash = events[0].Hash.ToUpperInvariant() };

        Assert.False(HashChainedAuditTrail.VerifyEvents(events));
    }

    private sealed class FixedTime(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
