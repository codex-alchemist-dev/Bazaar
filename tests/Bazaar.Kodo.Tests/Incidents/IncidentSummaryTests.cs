using System.Text;
using Bazaar.Kodo.Incidents;
using Bazaar.Kodo.Journal;
using Bazaar.Kodo.Responder;

namespace Bazaar.Kodo.Tests.Incidents;

public sealed class IncidentSummaryTests
{
    private static readonly DateTimeOffset _t0 = new(2026, 11, 19, 12, 0, 0, TimeSpan.Zero);
    private static readonly TimeSpan _lookBack = TimeSpan.FromHours(1);

    private static IncidentRecord Incident(IncidentPhase phase = IncidentPhase.Closed)
    {
        IncidentTransition[] all =
        [
            new(0, IncidentPhase.Opened, IncidentPhase.Frozen, _t0.AddSeconds(5), "kodo", "freeze_applied", null),
            new(1, IncidentPhase.Frozen, IncidentPhase.Investigating, _t0.AddMinutes(1), "owner", "looking", null),
            new(2, IncidentPhase.Investigating, IncidentPhase.Recovering, _t0.AddMinutes(2), "owner", "run_started", null),
            new(3, IncidentPhase.Recovering, IncidentPhase.Resolved, _t0.AddMinutes(9), "owner", "green", null),
            new(4, IncidentPhase.Resolved, IncidentPhase.Closed, _t0.AddMinutes(10), "owner", "confirmed", "ap-1"),
        ];
        int count = phase switch
        {
            IncidentPhase.Opened => 0,
            IncidentPhase.Frozen => 1,
            IncidentPhase.Investigating => 2,
            IncidentPhase.Recovering => 3,
            IncidentPhase.Resolved => 4,
            _ => 5,
        };

        return new IncidentRecord(
            1, 3, "inc-000003", AlertSeverity.Critical, IncidentTrigger.CriticalProbeRed, "host.disk.free:data", "Data disk full", "kodo", "auto_trigger",
            _t0, phase, ["host"], ["host.disk.free:data"], [], [new IncidentRunLink("inc-000003-r1", "fix.disk_space", RunbookPhase.Succeeded)],
            all.Take(count).ToList(), true, phase == IncidentPhase.Closed, "ab12", 5, _t0, 9);
    }

    private static List<JournalRecord> Journal(int count, string kind = "state_change", int spacingSeconds = 30)
    {
        List<JournalRecord> records = [];
        byte[] previous = JournalChain.GenesisHash();
        for (int i = 0; i < count; i++)
        {
            JournalRecord record = JournalChain.Create(i, _t0.AddSeconds((i * spacingSeconds) - 60), kind, Encoding.UTF8.GetBytes("payload-secret-" + i), previous);
            records.Add(record);
            previous = record.Hash;
        }

        return records;
    }

    private static IReadOnlyList<RunbookJournalEntry> Runs() =>
    [
        new("inc-000003-r1", 0, _t0.AddMinutes(2), RunbookJournalKinds.RunStarted, null, "gated"),
        new("inc-000003-r1", 1, _t0.AddMinutes(3), RunbookJournalKinds.StepStarted, "fix", null),
        new("inc-000003-r1", 2, _t0.AddMinutes(4), RunbookJournalKinds.RunFinished, null, "succeeded"),
    ];

    private static IncidentSummaryInput Input(IncidentRecord? incident = null, IReadOnlyList<JournalRecord>? journal = null, int revision = 1) =>
        new(incident ?? Incident(), journal ?? Journal(6), Runs(), _t0.AddMinutes(30), revision);

    [Fact]
    public void TheSameInputsGiveTheSameBytes()
    {
        IncidentSummary one = IncidentSummaryBuilder.Build(Input(), _lookBack);
        IncidentSummary two = IncidentSummaryBuilder.Build(Input(), _lookBack);

        Assert.Equal(IncidentSummaryRenderer.RenderBytes(one), IncidentSummaryRenderer.RenderBytes(two));
        Assert.Equal(IncidentCodec.ToUtf8Bytes(one), IncidentCodec.ToUtf8Bytes(two));
    }

    [Fact]
    public void TheOrderOfTheInputListsDoesNotChangeTheTimelineForDistinctTimes()
    {
        IncidentSummary forward = IncidentSummaryBuilder.Build(Input(), _lookBack);
        IncidentSummaryInput shuffled = Input() with { RunEntries = [.. Runs().Reverse()] };
        IncidentSummary other = IncidentSummaryBuilder.Build(shuffled, _lookBack);

        Assert.Equal(forward.Timeline, other.Timeline);
    }

    [Fact]
    public void TheTimelineIsSortedByTimeThenSourceAndHoldsTheIncidentTransitions()
    {
        IncidentSummary summary = IncidentSummaryBuilder.Build(Input(), _lookBack);

        Assert.Equal(summary.Timeline.OrderBy(e => e.At).Select(e => e.At), summary.Timeline.Select(e => e.At));
        Assert.Contains(summary.Timeline, e => e is { Source: SummarySource.Incident, Kind: "opened", Actor: "kodo" });
        Assert.Contains(summary.Timeline, e => e is { Source: SummarySource.Incident, Kind: "recovering_to_resolved", Actor: "owner", Detail: "green" });
        Assert.Contains(summary.Timeline, e => e is { Source: SummarySource.Runbook, Kind: "step_started", Detail: "step=fix" });
        Assert.Equal(5, summary.Facts.TransitionCount);
        Assert.Equal(3, summary.Facts.RunEntryCount);
        Assert.Equal(_t0.AddMinutes(10), summary.ClosedAt);
    }

    [Fact]
    public void JournalRecordsOutsideTheWindowAreLeftOutAndPayloadsAreNeverShown()
    {
        List<JournalRecord> journal = Journal(3);
        journal.Add(JournalChain.Create(3, _t0.AddHours(5), "late", [1], journal[^1].Hash));
        IncidentSummary summary = IncidentSummaryBuilder.Build(Input(journal: journal), _lookBack);
        string text = IncidentSummaryRenderer.Render(summary);

        Assert.Equal(3, summary.Facts.JournalRecordsInWindow);
        Assert.DoesNotContain("payload-secret", text, StringComparison.Ordinal);
        Assert.Contains("bytes=16", text, StringComparison.Ordinal);
        Assert.DoesNotContain("late", text, StringComparison.Ordinal);
    }

    [Fact]
    public void ABrokenJournalChainIsStatedNotHidden()
    {
        List<JournalRecord> journal = Journal(4);
        journal[2] = journal[2] with { Payload = [9, 9] };
        IncidentSummary summary = IncidentSummaryBuilder.Build(Input(journal: journal), _lookBack);

        Assert.False(summary.Facts.JournalChainValid);
        Assert.Equal(2, summary.Facts.JournalFirstBrokenIndex);
        Assert.Contains("journal_chain_valid: false", IncidentSummaryRenderer.Render(summary), StringComparison.Ordinal);
    }

    [Fact]
    public void DerivedDurationsAreLabelledAndMissingOnesAreNotInvented()
    {
        IncidentSummary closed = IncidentSummaryBuilder.Build(Input(), _lookBack);
        Assert.Equal(new SummaryDurations(5, 540, 600), closed.Derived);

        IncidentSummary open = IncidentSummaryBuilder.Build(Input(Incident(IncidentPhase.Investigating)), _lookBack);
        Assert.Equal(new SummaryDurations(5, null, null), open.Derived);
        Assert.Null(open.ClosedAt);
        string text = IncidentSummaryRenderer.Render(open);
        Assert.Contains("Derived (computed from timestamps):", text, StringComparison.Ordinal);
        Assert.Contains("seconds_to_resolve: n/a", text, StringComparison.Ordinal);
        Assert.Contains("Closed: not closed", text, StringComparison.Ordinal);
    }

    [Fact]
    public void ItIsALabelledDraftWithNoModelText()
    {
        IncidentSummary summary = IncidentSummaryBuilder.Build(Input(revision: 2), _lookBack);
        string text = IncidentSummaryRenderer.Render(summary);

        Assert.True(summary.IsDraft);
        Assert.StartsWith("INCIDENT inc-000003 (draft, revision 2)\n", text, StringComparison.Ordinal);
        Assert.Contains("Inferred: none. This draft holds no model text.", text, StringComparison.Ordinal);
    }

    [Fact]
    public void ControlCharactersLineBreaksAndBidiOverridesAreEscaped()
    {
        string hostile = "kind\nFORGED 2026-01-01T00:00:00Z by root\r\u001b[31m\u202Eevil\u2028tab\there\\end";
        List<JournalRecord> journal = [JournalChain.Create(0, _t0, hostile, [1], JournalChain.GenesisHash())];
        IncidentRecord incident = Incident() with { Cause = "a:b", Opener = "owner" };
        IncidentSummaryInput input = Input(incident, journal) with
        {
            RunEntries = [new RunbookJournalEntry("run1", 0, _t0, hostile, hostile, hostile)],
        };

        string text = IncidentSummaryRenderer.Render(IncidentSummaryBuilder.Build(input, _lookBack));

        Assert.DoesNotContain('\u001b', text);
        Assert.DoesNotContain('\u202E', text);
        Assert.DoesNotContain('\u2028', text);
        Assert.DoesNotContain('\r', text);
        Assert.DoesNotContain("\nFORGED", text, StringComparison.Ordinal);
        Assert.Contains("kind\\nFORGED", text, StringComparison.Ordinal);
        Assert.Contains("\\u{1B}[31m\\u{202E}evil\\u{2028}tab\\there\\\\end", text, StringComparison.Ordinal);
        Assert.All(text.Split('\n').Where(l => l.Length > 0 && char.IsDigit(l[0])), line => Assert.Matches(@"^\d{4}-\d\d-\d\dT\d\d:\d\d:\d\dZ (incident|runbook|journal) ", line));
    }

    [Fact]
    public void EscapeHandlesEdgeCases()
    {
        Assert.Equal(string.Empty, IncidentSummaryRenderer.Escape(null));
        Assert.Equal("plain text", IncidentSummaryRenderer.Escape("plain text"));
        Assert.Equal("caf\u00e9 \U0001F600", IncidentSummaryRenderer.Escape("caf\u00e9 \U0001F600"));
        Assert.Equal("\\u{FFFD}", IncidentSummaryRenderer.Escape("\ud800"));
        Assert.Equal("\\u{7F}\\u{85}", IncidentSummaryRenderer.Escape("\u007f\u0085"));

        string escaped = IncidentSummaryRenderer.Escape(new string('x', 1000));
        Assert.Equal(IncidentSummaryRenderer.MaxValueLength + 3, escaped.Length);
        Assert.EndsWith("...", escaped, StringComparison.Ordinal);
    }

    [Fact]
    public void TheOutputHasNoByteOrderMarkOrCarriageReturns()
    {
        byte[] bytes = IncidentSummaryRenderer.RenderBytes(IncidentSummaryBuilder.Build(Input(), _lookBack));

        Assert.NotEqual(0xEF, bytes[0]);
        Assert.DoesNotContain((byte)'\r', bytes);
        Assert.Equal((byte)'\n', bytes[^1]);
    }

    [Fact]
    public void TheFactCheckPassesForAnHonestDraftAndNamesEachWrongNumber()
    {
        IncidentSummaryInput input = Input();
        IncidentSummary summary = IncidentSummaryBuilder.Build(input, _lookBack);
        Assert.Empty(IncidentSummaryBuilder.FactCheck(summary, input, _lookBack));

        IncidentSummary wrong = summary with
        {
            Facts = summary.Facts with { JournalRecordsInWindow = 99, RunEntryCount = 0 },
            Derived = new SummaryDurations(1, 2, 3),
        };

        Assert.Equal(
            ["journal_records_in_window", "run_entry_count", "derived_durations"],
            IncidentSummaryBuilder.FactCheck(wrong, input, _lookBack));
    }

    [Fact]
    public void TheTimelineIsBoundedAndTheOmissionIsCounted()
    {
        List<JournalRecord> journal = Journal(700, "tick", 1);
        IncidentSummary summary = IncidentSummaryBuilder.Build(
            Input(journal: journal) with { GeneratedAt = _t0.AddHours(2) }, TimeSpan.FromHours(3));

        Assert.Equal(IncidentSummaryBuilder.MaxTimeline, summary.Timeline.Count);
        Assert.True(summary.Facts.OmittedJournalEntries > 0);
        Assert.Contains(summary.Timeline, e => e.Source == SummarySource.Incident && e.Kind == "opened");
        Assert.Equal(5, summary.Timeline.Count(e => e.Source == SummarySource.Incident && e.Kind != "opened"));
        Assert.Equal(3, summary.Timeline.Count(e => e.Source == SummarySource.Runbook));
    }

    [Fact]
    public void ASummarySurvivesJson()
    {
        IncidentSummary summary = IncidentSummaryBuilder.Build(Input(), _lookBack);
        byte[] bytes = IncidentCodec.ToUtf8Bytes(summary);

        Assert.StartsWith("{\"version\":1,\"incidentId\":\"inc-000003\"", Encoding.UTF8.GetString(bytes), StringComparison.Ordinal);
        Assert.Contains("\"severity\":\"Critical\"", Encoding.UTF8.GetString(bytes), StringComparison.Ordinal);
    }
}
