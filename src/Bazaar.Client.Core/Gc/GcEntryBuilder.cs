using Bazaar.Contracts.Gc;

namespace Bazaar.Client.Core.Gc;

/// <summary>
/// Turns one GC's log rows into timeline entries for one viewer. Edits fold into their message, rows
/// are ordered by seq, and consecutive rows from one author are grouped by time. Pure: no I/O.
/// </summary>
public sealed class GcEntryBuilder
{
    /// <summary>The default grouping window, client.gc.group_window_min (5 minutes).</summary>
    public static readonly TimeSpan DefaultGroupWindow = TimeSpan.FromMinutes(5);

    private static readonly HashSet<string> _knownKinds = new(StringComparer.Ordinal)
    {
        GcEventKinds.Message,
        GcEventKinds.MessageEdit,
        GcEventKinds.System,
        GcEventKinds.Structured,
        GcEventKinds.Quarantine,
        GcEventKinds.Handover,
        GcEventKinds.ExternalMail,
    };

    private readonly TimeZoneInfo _viewerZone;
    private readonly TimeSpan _groupWindow;

    /// <param name="viewerZone">The viewer's time zone, used for date separators and local times.</param>
    /// <param name="groupWindow">Longest gap between two rows from one author that still groups them. Defaults to <see cref="DefaultGroupWindow"/>.</param>
    public GcEntryBuilder(TimeZoneInfo viewerZone, TimeSpan? groupWindow = null)
    {
        ArgumentNullException.ThrowIfNull(viewerZone);

        var window = groupWindow ?? DefaultGroupWindow;
        if (window < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(groupWindow), window, "The group window cannot be negative.");
        }

        _viewerZone = viewerZone;
        _groupWindow = window;
    }

    /// <summary>
    /// Builds the timeline for one GC.
    /// </summary>
    /// <param name="events">Log rows of one GC. Duplicate sequence numbers are shown once.</param>
    /// <param name="messages">Message identities and current versions. Messages of other GCs are ignored.</param>
    /// <param name="principals">Principals who may appear as authors. Unknown authors get a null name.</param>
    /// <returns>Entries in sequence order.</returns>
    public IReadOnlyList<GcEntryViewModel> Build(
        IEnumerable<GcEvent> events,
        IEnumerable<GcMessage> messages,
        IEnumerable<GcPrincipal> principals)
    {
        ArgumentNullException.ThrowIfNull(events);
        ArgumentNullException.ThrowIfNull(messages);
        ArgumentNullException.ThrowIfNull(principals);

        var ordered = events.OrderBy(e => e.Seq).DistinctBy(e => e.Seq).ToList();
        if (ordered.Count == 0)
        {
            return [];
        }

        var gcId = ordered[0].GcId;
        if (ordered.Any(e => e.GcId != gcId))
        {
            throw new ArgumentException("All events must belong to one group chat.", nameof(events));
        }

        var messagesById = messages
            .Where(m => m.GcId == gcId)
            .DistinctBy(m => m.MessageId)
            .ToDictionary(m => m.MessageId);
        var principalsById = principals.DistinctBy(p => p.Id).ToDictionary(p => p.Id);

        var entries = new List<GcEntryViewModel>(ordered.Count);
        DateOnly? previousDay = null;
        Guid? previousAuthor = null;
        var previousCreated = DateTimeOffset.MinValue;

        foreach (var e in ordered)
        {
            // An edit is a version of an existing message, shown inside that message's row.
            if (e.Kind == GcEventKinds.MessageEdit)
            {
                continue;
            }

            var localTime = TimeZoneInfo.ConvertTime(e.CreatedAt, _viewerZone);
            var day = DateOnly.FromDateTime(localTime.DateTime);
            var separator = day == previousDay ? (DateOnly?)null : day;

            var gap = e.CreatedAt - previousCreated;
            var continuation = separator is null
                && previousAuthor == e.AuthorId
                && gap >= TimeSpan.Zero
                && gap <= _groupWindow;

            principalsById.TryGetValue(e.AuthorId, out var principal);
            var header = new GcEntryHeader(
                e.GcId,
                e.Seq,
                e.Kind,
                e.AuthorId,
                e.AuthorKind,
                principal?.DisplayName,
                principal?.Kind,
                e.SourceTrust,
                e.CreatedAt,
                localTime,
                separator,
                continuation);

            entries.Add(CreateEntry(e, header, messagesById));

            previousDay = day;
            previousAuthor = e.AuthorId;
            previousCreated = e.CreatedAt;
        }

        return entries;
    }

    private static GcEntryViewModel CreateEntry(
        GcEvent e,
        GcEntryHeader header,
        Dictionary<Guid, GcMessage> messagesById)
    {
        if (e.Kind == GcEventKinds.Message)
        {
            GcMessage? message = e.MessageId is { } id && messagesById.TryGetValue(id, out var found) ? found : null;
            return new GcMessageEntryViewModel(
                header,
                e.MessageId,
                message?.Body,
                message?.Version ?? e.Version ?? 1,
                message?.Edited ?? false);
        }

        if (_knownKinds.Contains(e.Kind))
        {
            return new GcEventEntryViewModel(header, e.PayloadJson, e.TargetSeq, e.ReplyToSeq);
        }

        return new GcUnknownEntryViewModel(header);
    }
}
