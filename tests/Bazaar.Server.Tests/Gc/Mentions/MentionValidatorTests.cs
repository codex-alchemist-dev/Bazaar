using Bazaar.Server.Gc.Mentions;

namespace Bazaar.Server.Tests.Gc.Mentions;

public class MentionValidatorTests
{
    private static Guid Owner => Id(1);
    private static Guid Author => Id(2);
    private static Guid Ana => Id(3);
    private static Guid Bob => Id(4);
    private static Guid Cy => Id(5);

    private const string Body = "hi @Ana";

    private static Guid Id(int n) => new(n, 0, 0, new byte[8]);

    private static HashSet<Guid> Active(params Guid[] ids) => new(ids);

    [Fact]
    public void Valid_span_to_active_member_is_accepted()
    {
        var result = MentionValidator.Validate(
            Body,
            [new MentionSpan(3, 4, Ana)],
            Active(Ana),
            Owner,
            Author);

        var accepted = Assert.Single(result.Accepted);
        Assert.Equal(new AcceptedMention(Ana, 3, 4), accepted);
        Assert.Empty(result.Rejected);
        Assert.Equal("@Ana", Body.Substring(accepted.Start, accepted.Length));
    }

    [Theory]
    [InlineData(-1, 2)]
    [InlineData(2, -1)]
    [InlineData(5, 3)]
    [InlineData(0, 8)]
    public void Span_outside_body_is_out_of_bounds(int start, int length)
    {
        var result = MentionValidator.Validate(
            Body,
            [new MentionSpan(start, length, Ana)],
            Active(Ana),
            Owner,
            Author);

        Assert.Empty(result.Accepted);
        var rejected = Assert.Single(result.Rejected);
        Assert.Equal(MentionRejectReason.OutOfBounds, rejected.Reason);
        Assert.False(rejected.InviteSuggested);
    }

    [Fact]
    public void Span_ending_exactly_at_body_end_is_accepted()
    {
        // Body length is 7 UTF-16 units; start 3 + length 4 = 7 is the last valid span.
        var result = MentionValidator.Validate(
            Body,
            [new MentionSpan(3, 4, Ana)],
            Active(Ana),
            Owner,
            Author);

        Assert.Single(result.Accepted);
    }

    [Fact]
    public void Zero_length_span_is_empty()
    {
        var result = MentionValidator.Validate(
            Body,
            [new MentionSpan(3, 0, Ana)],
            Active(Ana),
            Owner,
            Author);

        Assert.Empty(result.Accepted);
        var rejected = Assert.Single(result.Rejected);
        Assert.Equal(MentionRejectReason.Empty, rejected.Reason);
    }

    [Fact]
    public void Second_of_two_overlapping_spans_is_rejected()
    {
        // "hi @Ana and @Bob": Ana covers 3..6, Bob's span 5..7 overlaps it at 5 and 6.
        const string body = "hi @Ana and @Bob";
        var result = MentionValidator.Validate(
            body,
            [new MentionSpan(3, 4, Ana), new MentionSpan(5, 3, Bob)],
            Active(Ana, Bob),
            Owner,
            Author);

        var accepted = Assert.Single(result.Accepted);
        Assert.Equal(Ana, accepted.TargetPrincipalId);
        var rejected = Assert.Single(result.Rejected);
        Assert.Equal(new MentionSpan(5, 3, Bob), rejected.Span);
        Assert.Equal(MentionRejectReason.Overlapping, rejected.Reason);
    }

    [Fact]
    public void Overlap_is_decided_after_sorting_not_by_input_order()
    {
        const string body = "hi @Ana and @Bob";
        var result = MentionValidator.Validate(
            body,
            [new MentionSpan(5, 3, Bob), new MentionSpan(3, 4, Ana)],
            Active(Ana, Bob),
            Owner,
            Author);

        // Ana starts first in the body, so Ana is kept whatever order the client sent.
        Assert.Equal(Ana, Assert.Single(result.Accepted).TargetPrincipalId);
        Assert.Equal(new MentionSpan(5, 3, Bob), Assert.Single(result.Rejected).Span);
    }

    [Fact]
    public void Adjacent_spans_do_not_overlap()
    {
        const string body = "@Ana@Bob";
        var result = MentionValidator.Validate(
            body,
            [new MentionSpan(0, 4, Ana), new MentionSpan(4, 4, Bob)],
            Active(Ana, Bob),
            Owner,
            Author);

        Assert.Equal(2, result.Accepted.Count);
        Assert.Empty(result.Rejected);
    }

    [Fact]
    public void Non_member_is_rejected_with_invite_suggested()
    {
        var result = MentionValidator.Validate(
            "hi @Cy",
            [new MentionSpan(3, 3, Cy)],
            Active(Ana),
            Owner,
            Author);

        Assert.Empty(result.Accepted);
        var rejected = Assert.Single(result.Rejected);
        Assert.Equal(MentionRejectReason.NotAMember, rejected.Reason);
        Assert.True(rejected.InviteSuggested);
    }

    [Fact]
    public void Former_member_absent_from_active_set_is_rejected_with_invite_suggested()
    {
        // Bob left the GC: he is not in the active set any more, so the span is refused.
        const string body = "hi @Bob";
        var result = MentionValidator.Validate(
            body,
            [new MentionSpan(3, 4, Bob)],
            Active(Ana),
            Owner,
            Author);

        Assert.Empty(result.Accepted);
        var rejected = Assert.Single(result.Rejected);
        Assert.Equal(MentionRejectReason.NotAMember, rejected.Reason);
        Assert.True(rejected.InviteSuggested);
    }

    [Fact]
    public void Owner_is_accepted_when_absent_from_active_set()
    {
        var result = MentionValidator.Validate(
            "hi @owner",
            [new MentionSpan(3, 6, Owner)],
            Active(Ana),
            Owner,
            Author);

        Assert.Equal(new AcceptedMention(Owner, 3, 6), Assert.Single(result.Accepted));
        Assert.Empty(result.Rejected);
    }

    [Fact]
    public void Non_member_span_does_not_block_an_overlapping_member_span()
    {
        // The rejected span occupies no range, so the valid span over the same text is kept.
        var result = MentionValidator.Validate(
            Body,
            [new MentionSpan(3, 4, Cy), new MentionSpan(3, 4, Ana)],
            Active(Ana),
            Owner,
            Author);

        Assert.Equal(Ana, Assert.Single(result.Accepted).TargetPrincipalId);
        var rejected = Assert.Single(result.Rejected);
        Assert.Equal(MentionRejectReason.NotAMember, rejected.Reason);
    }

    [Fact]
    public void Plain_at_text_without_span_creates_no_mention()
    {
        var result = MentionValidator.Validate(
            "hi @Ana, are you there?",
            [],
            Active(Ana),
            Owner,
            Author);

        Assert.Empty(result.Accepted);
        Assert.Empty(result.Rejected);
    }

    [Fact]
    public void Self_mention_is_accepted_by_the_validator()
    {
        // The inbox planner skips the author; validation keeps the mention.
        var result = MentionValidator.Validate(
            "hi @Ana",
            [new MentionSpan(3, 4, Ana)],
            Active(Ana),
            Owner,
            Ana);

        Assert.Equal(new AcceptedMention(Ana, 3, 4), Assert.Single(result.Accepted));
        Assert.Empty(result.Rejected);
    }

    [Fact]
    public void Offsets_are_utf16_code_units_so_a_surrogate_pair_counts_twice()
    {
        // "😀" is U+1F600: two UTF-16 units (a surrogate pair) but one code point.
        // Body layout in UTF-16 units: h=0 i=1 space=2 emoji=3,4 space=5 @=6 A=7 n=8 a=9. Length 10.
        const string body = "hi 😀 @Ana";
        Assert.Equal(10, body.Length);
        Assert.Equal(9, body.EnumerateRunes().Count());

        var valid = MentionValidator.Validate(
            body,
            [new MentionSpan(6, 4, Ana)],
            Active(Ana),
            Owner,
            Author);

        var accepted = Assert.Single(valid.Accepted);
        Assert.Equal(new AcceptedMention(Ana, 6, 4), accepted);
        Assert.Equal("@Ana", body.Substring(accepted.Start, accepted.Length));

        // Length 5 would need 11 units, which the body does not have. A code-point count
        // (9 for this body) would have allowed the same span; UTF-16 refuses it.
        var past = MentionValidator.Validate(
            body,
            [new MentionSpan(6, 5, Ana)],
            Active(Ana),
            Owner,
            Author);

        Assert.Empty(past.Accepted);
        Assert.Equal(MentionRejectReason.OutOfBounds, Assert.Single(past.Rejected).Reason);
    }
}
