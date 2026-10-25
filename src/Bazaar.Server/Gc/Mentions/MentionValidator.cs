namespace Bazaar.Server.Gc.Mentions;

/// <summary>Why a mention span was refused.</summary>
public enum MentionRejectReason
{
    /// <summary>The span starts before 0, has a negative length, or ends past the body (UTF-16 units).</summary>
    OutOfBounds,

    /// <summary>The span has zero length and covers no text.</summary>
    Empty,

    /// <summary>The span overlaps an earlier span that was kept (checked after sorting by start).</summary>
    Overlapping,

    /// <summary>The target is not an active member. Grants no access; an invite may be offered.</summary>
    NotAMember,
}

/// <summary>A span that passed validation and will produce a <c>gc_mention</c> row.</summary>
/// <param name="TargetPrincipalId">The validated target.</param>
/// <param name="Start">UTF-16 offset, copied from the span.</param>
/// <param name="Length">UTF-16 length, copied from the span.</param>
public sealed record AcceptedMention(Guid TargetPrincipalId, int Start, int Length);

/// <summary>A span that was refused, with the reason.</summary>
/// <param name="Span">The span as the client sent it.</param>
/// <param name="Reason">Why it was refused.</param>
/// <param name="InviteSuggested">True for <see cref="MentionRejectReason.NotAMember"/>: the client may offer an invite.</param>
public sealed record RejectedMention(MentionSpan Span, MentionRejectReason Reason, bool InviteSuggested);

/// <summary>Result of validating a message's spans.</summary>
/// <param name="Accepted">Accepted mentions, sorted by start offset.</param>
/// <param name="Rejected">Refused spans, in the order the client sent them.</param>
public sealed record MentionValidation(
    IReadOnlyList<AcceptedMention> Accepted,
    IReadOnlyList<RejectedMention> Rejected);

/// <summary>
/// Pure validation of structured mention spans against the GC's current active members. No I/O:
/// the caller loads <paramref name="activeMemberIds"/> and writes the result.
/// </summary>
/// <remarks>
/// Checks, in order, for each span: bounds (UTF-16 units), then emptiness, then membership. The
/// owner is always accepted, even when absent from the active set. The surviving spans are then
/// sorted by start and checked for overlap; the later span of an overlapping pair is rejected.
/// A rejected span does not occupy its range, so a valid span may overlap a rejected one.
/// A self-mention (target equals author) is accepted here; the inbox planner skips it.
/// Broadcast handles (@here, @all) are not spans and are handled by <see cref="BroadcastPolicy"/>.
/// </remarks>
public static class MentionValidator
{
    /// <summary>Validates the spans of one message body.</summary>
    /// <param name="body">Message text. Spans index into it in UTF-16 code units.</param>
    /// <param name="spans">Spans as the client sent them.</param>
    /// <param name="activeMemberIds">Principals that are active members of the GC now.</param>
    /// <param name="ownerId">The owner, always an available target.</param>
    /// <param name="authorId">The principal who wrote the message.</param>
    public static MentionValidation Validate(
        string body,
        IReadOnlyList<MentionSpan> spans,
        IReadOnlySet<Guid> activeMemberIds,
        Guid ownerId,
        Guid authorId)
    {
        ArgumentNullException.ThrowIfNull(body);
        ArgumentNullException.ThrowIfNull(spans);
        ArgumentNullException.ThrowIfNull(activeMemberIds);

        var reasons = new MentionRejectReason?[spans.Count];
        var survivors = new List<(int Index, MentionSpan Span)>(spans.Count);

        for (var i = 0; i < spans.Count; i++)
        {
            var span = spans[i];
            var reason = Check(body, span, activeMemberIds, ownerId);
            if (reason is null)
            {
                survivors.Add((i, span));
            }
            else
            {
                reasons[i] = reason;
            }
        }

        // Stable sort: equal starts keep the client's order, so the outcome is deterministic.
        var sorted = survivors.OrderBy(s => s.Span.Start).ThenBy(s => s.Span.Length).ToList();
        var accepted = new List<AcceptedMention>(sorted.Count);
        var keptEnd = -1;

        foreach (var (index, span) in sorted)
        {
            if (span.Start < keptEnd)
            {
                reasons[index] = MentionRejectReason.Overlapping;
                continue;
            }

            accepted.Add(new AcceptedMention(span.PrincipalId, span.Start, span.Length));
            keptEnd = span.Start + span.Length;
        }

        var rejected = new List<RejectedMention>();
        for (var i = 0; i < spans.Count; i++)
        {
            if (reasons[i] is { } reason)
            {
                rejected.Add(new RejectedMention(
                    spans[i],
                    reason,
                    reason == MentionRejectReason.NotAMember));
            }
        }

        return new MentionValidation(accepted, rejected);
    }

    private static MentionRejectReason? Check(string body, MentionSpan span, IReadOnlySet<Guid> activeMemberIds, Guid ownerId)
    {
        if (span.Start < 0 || span.Length < 0 || (long)span.Start + span.Length > body.Length)
        {
            return MentionRejectReason.OutOfBounds;
        }

        if (span.Length == 0)
        {
            return MentionRejectReason.Empty;
        }

        if (span.PrincipalId == ownerId || activeMemberIds.Contains(span.PrincipalId))
        {
            return null;
        }

        return MentionRejectReason.NotAMember;
    }
}
