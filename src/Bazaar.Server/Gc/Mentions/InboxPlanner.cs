using Bazaar.Server.Gc.Model;

namespace Bazaar.Server.Gc.Mentions;

/// <summary>
/// Pure planning of mention rows and inbox rows for one accepted message. The writer persists the
/// returned rows in the same transaction as the message event.
/// </summary>
public static class InboxPlanner
{
    /// <summary>
    /// Plans one inbox row per distinct mentioned principal, in first-mention order. The author
    /// never receives a row for their own mention. Repeated mentions of one person give one row.
    /// </summary>
    /// <param name="gc">The GC the message is in.</param>
    /// <param name="seq">Sequence number of the message event.</param>
    /// <param name="accepted">Accepted mentions from <see cref="MentionValidator.Validate"/>.</param>
    /// <param name="authorId">The principal who wrote the message.</param>
    public static IReadOnlyList<InboxRow> PlanMentionInbox(
        GcRecord gc,
        long seq,
        IEnumerable<AcceptedMention> accepted,
        Guid authorId)
    {
        ArgumentNullException.ThrowIfNull(gc);
        ArgumentNullException.ThrowIfNull(accepted);

        var seen = new HashSet<Guid>();
        var rows = new List<InboxRow>();

        foreach (var mention in accepted)
        {
            if (mention.TargetPrincipalId == authorId || !seen.Add(mention.TargetPrincipalId))
            {
                continue;
            }

            rows.Add(new InboxRow(mention.TargetPrincipalId, gc.GcId, seq, InboxReason.Mention));
        }

        return rows;
    }

    /// <summary>
    /// Maps accepted mentions to <c>gc_mention</c> rows. Self-mentions are kept (they are accepted
    /// mentions); duplicate targets collapse to one row, so the key (gc_id, seq, target) is unique.
    /// </summary>
    /// <param name="gc">The GC the message is in.</param>
    /// <param name="seq">Sequence number of the message event.</param>
    /// <param name="accepted">Accepted mentions from <see cref="MentionValidator.Validate"/>.</param>
    public static IReadOnlyList<GcMentionRow> PlanMentionRows(
        GcRecord gc,
        long seq,
        IEnumerable<AcceptedMention> accepted)
    {
        ArgumentNullException.ThrowIfNull(gc);
        ArgumentNullException.ThrowIfNull(accepted);

        var seen = new HashSet<Guid>();
        var rows = new List<GcMentionRow>();

        foreach (var mention in accepted)
        {
            if (seen.Add(mention.TargetPrincipalId))
            {
                rows.Add(new GcMentionRow(gc.GcId, seq, mention.TargetPrincipalId));
            }
        }

        return rows;
    }
}
