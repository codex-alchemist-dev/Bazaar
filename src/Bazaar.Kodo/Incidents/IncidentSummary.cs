using System.Globalization;
using System.Text;
using Bazaar.Kodo.Journal;
using Bazaar.Kodo.Responder;

namespace Bazaar.Kodo.Incidents;

/// <summary>Where a timeline entry came from.</summary>
public enum SummarySource
{
    Incident,
    Runbook,
    Journal,
}

/// <summary>One timeline line. Every text field is data from the records and is escaped when rendered.</summary>
/// <param name="At">When.</param>
/// <param name="Source">Which record set it came from.</param>
/// <param name="Kind">The record kind, transition word or run entry kind.</param>
/// <param name="Actor">Who, when the record says.</param>
/// <param name="Detail">A short code, a journal hash prefix and payload length, or null.</param>
public sealed record SummaryEntry(DateTimeOffset At, SummarySource Source, string Kind, string? Actor, string? Detail);

/// <summary>
/// The numbers a summary states, all counted from the records (section 16.4: a fact-check pass compares each stated number with the journal).
/// </summary>
public sealed record SummaryFacts(
    int JournalRecordsInWindow,
    bool JournalChainValid,
    int JournalFirstBrokenIndex,
    int TransitionCount,
    int RunEntryCount,
    int RunsLinked,
    int AlertsLinked,
    int EvidenceItems,
    string EvidenceRoot,
    int OmittedJournalEntries)
{
    /// <summary>Every fact as a name and a value, for the fact-check pass.</summary>
    public IReadOnlyList<(string Name, string Value)> Pairs() =>
    [
        ("journal_records_in_window", JournalRecordsInWindow.ToString(CultureInfo.InvariantCulture)),
        ("journal_chain_valid", JournalChainValid ? "true" : "false"),
        ("journal_first_broken_index", JournalFirstBrokenIndex.ToString(CultureInfo.InvariantCulture)),
        ("transition_count", TransitionCount.ToString(CultureInfo.InvariantCulture)),
        ("run_entry_count", RunEntryCount.ToString(CultureInfo.InvariantCulture)),
        ("runs_linked", RunsLinked.ToString(CultureInfo.InvariantCulture)),
        ("alerts_linked", AlertsLinked.ToString(CultureInfo.InvariantCulture)),
        ("evidence_items", EvidenceItems.ToString(CultureInfo.InvariantCulture)),
        ("evidence_root", EvidenceRoot),
        ("omitted_journal_entries", OmittedJournalEntries.ToString(CultureInfo.InvariantCulture)),
    ];
}

/// <summary>Durations computed from timestamps. They are derived, not recorded, so the summary labels them so. Null when an end is missing.</summary>
public sealed record SummaryDurations(long? SecondsToFreeze, long? SecondsToResolve, long? SecondsToClose);

/// <summary>
/// A post-incident summary draft (section 16.4): a structured, deterministic record with no model text. It is always a draft here;
/// publishing and revisions are a later step that needs the exit control.
/// </summary>
public sealed record IncidentSummary(
    int Version,
    string IncidentId,
    int Revision,
    bool IsDraft,
    DateTimeOffset GeneratedAt,
    AlertSeverity Severity,
    IncidentTrigger Trigger,
    string Cause,
    string Title,
    string Opener,
    IncidentPhase Phase,
    DateTimeOffset OpenedAt,
    DateTimeOffset? ClosedAt,
    IReadOnlyList<string> Categories,
    IReadOnlyList<string> LinkedAlerts,
    SummaryFacts Facts,
    SummaryDurations Derived,
    IReadOnlyList<SummaryEntry> Timeline)
{
    /// <summary>The summary format version.</summary>
    public const int CurrentVersion = 1;
}

/// <summary>What the builder reads. All of it is data passed in; the builder reads no clock and does no I/O.</summary>
/// <param name="Incident">The incident record, with its transitions.</param>
/// <param name="JournalRecords">The Kodo journal records, in chain order.</param>
/// <param name="RunEntries">The runbook journal entries of the incident's runs.</param>
/// <param name="GeneratedAt">The time to stamp on the draft. The caller passes it from its clock.</param>
/// <param name="Revision">The revision number, starting at 1.</param>
public sealed record IncidentSummaryInput(
    IncidentRecord Incident,
    IReadOnlyList<JournalRecord> JournalRecords,
    IReadOnlyList<RunbookJournalEntry> RunEntries,
    DateTimeOffset GeneratedAt,
    int Revision);

/// <summary>
/// Builds a summary draft from the journal records, the runbook entries and the incident's transitions (section 16.4). The same inputs give
/// the same bytes: entries sort by time, then source, then input order. Journal payloads are never shown, only their length and a hash prefix.
/// </summary>
public static class IncidentSummaryBuilder
{
    /// <summary>The most timeline entries. Transitions and runbook entries are always kept; journal entries fill the rest.</summary>
    public const int MaxTimeline = 500;

    /// <summary>Builds the draft.</summary>
    /// <param name="input">The records.</param>
    /// <param name="journalLookBack">How far before the opening time journal records are included.</param>
    public static IncidentSummary Build(IncidentSummaryInput input, TimeSpan journalLookBack)
    {
        ArgumentNullException.ThrowIfNull(input);
        IncidentRecord incident = input.Incident;
        DateTimeOffset closedAt = incident.Phase == IncidentPhase.Closed ? incident.Transitions[^1].At : default;
        DateTimeOffset windowStart = incident.OpenedAt - journalLookBack;
        DateTimeOffset windowEnd = incident.Phase == IncidentPhase.Closed ? closedAt : input.GeneratedAt;

        List<(SummaryEntry Entry, int Order)> fixedEntries = [];
        fixedEntries.Add((new SummaryEntry(incident.OpenedAt, SummarySource.Incident, "opened", incident.Opener, incident.OpenReason), 0));
        foreach (IncidentTransition t in incident.Transitions)
        {
            fixedEntries.Add((new SummaryEntry(t.At, SummarySource.Incident, $"{Word(t.From)}_to_{Word(t.To)}", t.Actor, t.Reason), fixedEntries.Count));
        }

        foreach (RunbookJournalEntry run in input.RunEntries)
        {
            fixedEntries.Add((new SummaryEntry(run.At, SummarySource.Runbook, run.Kind, run.RunId, StepDetail(run)), fixedEntries.Count));
        }

        List<(SummaryEntry Entry, int Order)> journalEntries = [];
        foreach (JournalRecord record in input.JournalRecords.Where(r => r.At >= windowStart && r.At <= windowEnd))
        {
            journalEntries.Add((
                new SummaryEntry(
                    record.At,
                    SummarySource.Journal,
                    record.Kind,
                    null,
                    $"seq={record.Sequence.ToString(CultureInfo.InvariantCulture)} bytes={record.Payload.Length.ToString(CultureInfo.InvariantCulture)} hash={Convert.ToHexStringLower(record.Hash.AsSpan(0, Math.Min(6, record.Hash.Length)))}"),
                journalEntries.Count));
        }

        int room = Math.Max(0, MaxTimeline - fixedEntries.Count);
        int omitted = Math.Max(0, journalEntries.Count - room);
        IEnumerable<(SummaryEntry Entry, int Order)> kept = journalEntries.Take(room);

        List<SummaryEntry> timeline = fixedEntries.Concat(kept)
            .OrderBy(pair => pair.Entry.At)
            .ThenBy(pair => pair.Entry.Source)
            .ThenBy(pair => pair.Order)
            .Select(pair => pair.Entry)
            .Take(MaxTimeline)
            .ToList();

        JournalVerification chain = JournalChain.Verify(input.JournalRecords);
        SummaryFacts facts = new(
            JournalRecordsInWindow: journalEntries.Count,
            JournalChainValid: chain.IsValid,
            JournalFirstBrokenIndex: chain.FirstBrokenIndex ?? -1,
            TransitionCount: incident.Transitions.Count,
            RunEntryCount: input.RunEntries.Count,
            RunsLinked: incident.Runs.Count,
            AlertsLinked: incident.LinkedAlerts.Count,
            EvidenceItems: incident.EvidenceItems,
            EvidenceRoot: incident.EvidenceRoot ?? string.Empty,
            OmittedJournalEntries: omitted);

        return new IncidentSummary(
            IncidentSummary.CurrentVersion,
            incident.Id,
            input.Revision,
            true,
            input.GeneratedAt.ToUniversalTime(),
            incident.Severity,
            incident.Trigger,
            incident.Cause,
            incident.Title,
            incident.Opener,
            incident.Phase,
            incident.OpenedAt,
            incident.Phase == IncidentPhase.Closed ? closedAt : null,
            incident.Categories,
            incident.LinkedAlerts,
            facts,
            new SummaryDurations(SecondsTo(incident, IncidentPhase.Frozen), SecondsTo(incident, IncidentPhase.Resolved), SecondsTo(incident, IncidentPhase.Closed)),
            timeline);
    }

    /// <summary>
    /// The fact-check pass (section 16.4): recounts every stated number from the records and returns the names of those that differ.
    /// An empty list means every number matches. A draft is not publishable while this is not empty.
    /// </summary>
    public static IReadOnlyList<string> FactCheck(IncidentSummary summary, IncidentSummaryInput input, TimeSpan journalLookBack)
    {
        ArgumentNullException.ThrowIfNull(summary);
        IncidentSummary fresh = Build(input, journalLookBack);
        List<string> wrong = [];
        IReadOnlyList<(string Name, string Value)> stated = summary.Facts.Pairs();
        IReadOnlyList<(string Name, string Value)> counted = fresh.Facts.Pairs();
        for (int i = 0; i < counted.Count; i++)
        {
            if (i >= stated.Count || !string.Equals(stated[i].Value, counted[i].Value, StringComparison.Ordinal))
            {
                wrong.Add(counted[i].Name);
            }
        }

        if (summary.Derived != fresh.Derived)
        {
            wrong.Add("derived_durations");
        }

        return wrong;
    }

    private static long? SecondsTo(IncidentRecord incident, IncidentPhase phase)
    {
        IncidentTransition? last = incident.Transitions.LastOrDefault(t => t.To == phase);
        return last is null ? null : (long)(last.At - incident.OpenedAt).TotalSeconds;
    }

    private static string? StepDetail(RunbookJournalEntry run)
    {
        string? step = run.StepId;
        string? detail = run.Detail;
        return step is null ? detail : detail is null ? $"step={step}" : $"step={step} {detail}";
    }

    private static string Word(IncidentPhase phase) => phase.ToString().ToLowerInvariant();
}

/// <summary>
/// The plain-text rendering of a summary (section 16.4). No markup: every value taken from a record is escaped so a control character,
/// a line break or a bidirectional override in a record cannot forge a line or hide text. Output is UTF-8 with LF line ends.
/// </summary>
public static class IncidentSummaryRenderer
{
    /// <summary>The longest escaped value; longer values end in <c>...</c>.</summary>
    public const int MaxValueLength = 160;

    /// <summary>Renders the summary as text.</summary>
    public static string Render(IncidentSummary summary)
    {
        ArgumentNullException.ThrowIfNull(summary);
        StringBuilder text = new();
        text.Append("INCIDENT ").Append(Escape(summary.IncidentId)).Append(" (").Append(summary.IsDraft ? "draft" : "published")
            .Append(", revision ").Append(summary.Revision.ToString(CultureInfo.InvariantCulture)).Append(")\n");
        text.Append("Generated: ").Append(Time(summary.GeneratedAt)).Append('\n');
        text.Append("Title: ").Append(Escape(summary.Title)).Append('\n');
        text.Append("Severity: ").Append(Word(summary.Severity)).Append("; trigger: ").Append(Word(summary.Trigger))
            .Append("; cause: ").Append(Escape(summary.Cause)).Append('\n');
        text.Append("Opened: ").Append(Time(summary.OpenedAt)).Append(" by ").Append(Escape(summary.Opener)).Append('\n');
        text.Append("Closed: ").Append(summary.ClosedAt is { } closed ? Time(closed) : "not closed").Append("; phase: ").Append(Word(summary.Phase)).Append('\n');
        text.Append("Categories: ").Append(Join(summary.Categories)).Append('\n');
        text.Append("Linked alerts: ").Append(Join(summary.LinkedAlerts)).Append('\n');

        text.Append("\nKnown (counted from the records):\n");
        foreach ((string name, string value) in summary.Facts.Pairs())
        {
            text.Append("- ").Append(name).Append(": ").Append(Escape(value)).Append('\n');
        }

        text.Append("\nDerived (computed from timestamps):\n");
        text.Append("- seconds_to_freeze: ").Append(Seconds(summary.Derived.SecondsToFreeze)).Append('\n');
        text.Append("- seconds_to_resolve: ").Append(Seconds(summary.Derived.SecondsToResolve)).Append('\n');
        text.Append("- seconds_to_close: ").Append(Seconds(summary.Derived.SecondsToClose)).Append('\n');

        text.Append("\nInferred: none. This draft holds no model text.\n");

        text.Append("\nTimeline:\n");
        foreach (SummaryEntry entry in summary.Timeline)
        {
            text.Append(Time(entry.At)).Append(' ').Append(Word(entry.Source)).Append(' ').Append(Escape(entry.Kind));
            if (entry.Actor is not null)
            {
                text.Append(" by ").Append(Escape(entry.Actor));
            }

            if (entry.Detail is not null)
            {
                text.Append(": ").Append(Escape(entry.Detail));
            }

            text.Append('\n');
        }

        return text.ToString();
    }

    /// <summary>The rendering as UTF-8 bytes without a byte order mark.</summary>
    public static byte[] RenderBytes(IncidentSummary summary) => new UTF8Encoding(false).GetBytes(Render(summary));

    /// <summary>
    /// Escapes a value for one line of plain text: backslash, line breaks, tabs, control characters, bidirectional controls, line and
    /// paragraph separators and unpaired surrogates become visible escapes; long values are cut.
    /// </summary>
    public static string Escape(string? value)
    {
        if (value is null)
        {
            return string.Empty;
        }

        StringBuilder text = new();
        int written = 0;
        foreach (Rune rune in value.EnumerateRunes())
        {
            string piece = EscapeRune(rune);
            if (written + piece.Length > MaxValueLength)
            {
                text.Append("...");
                break;
            }

            text.Append(piece);
            written += piece.Length;
        }

        return text.ToString();
    }

    private static string EscapeRune(Rune rune)
    {
        int v = rune.Value;
        return v switch
        {
            '\\' => "\\\\",
            '\n' => "\\n",
            '\r' => "\\r",
            '\t' => "\\t",
            _ when v < 0x20 || (v >= 0x7F && v <= 0x9F) || v is 0x2028 or 0x2029 or 0x061C or 0x200E or 0x200F or 0xFEFF or 0xFFFD
                || (v >= 0x202A && v <= 0x202E) || (v >= 0x2066 && v <= 0x2069) || (v >= 0x200B && v <= 0x200D)
                => string.Create(CultureInfo.InvariantCulture, $"\\u{{{v:X}}}"),
            _ => rune.ToString(),
        };
    }

    private static string Join(IReadOnlyList<string> values) => values.Count == 0 ? "none" : string.Join(", ", values.Select(Escape));

    private static string Seconds(long? value) => value?.ToString(CultureInfo.InvariantCulture) ?? "n/a";

    private static string Time(DateTimeOffset at) => at.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture);

    private static string Word<T>(T value)
        where T : struct, Enum => value.ToString().ToLowerInvariant();
}
