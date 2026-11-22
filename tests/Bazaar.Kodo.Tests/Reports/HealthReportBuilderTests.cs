using System.Text;
using Bazaar.Contracts.Kodo;
using Bazaar.Kodo.Evaluator;
using Bazaar.Kodo.Reports;

namespace Bazaar.Kodo.Tests.Reports;

public sealed class HealthReportBuilderTests
{
    private static readonly DateTimeOffset _start = ReportTestKit.Utc(2026, 11, 21, 6);
    private static readonly DateTimeOffset _end = ReportTestKit.Utc(2026, 11, 22, 6);

    private static HealthReport Build(ReportInputs? inputs = null) =>
        HealthReportBuilder.Build(ReportTestKit.Definition(), _start, _end, inputs ?? ReportTestKit.Inputs(), ReportTestKit.Now);

    [Fact]
    public void Header_and_snapshot_fields_are_copied()
    {
        HealthReport report = Build();

        Assert.Equal("daily_owner-20261122T060000Z", report.Id);
        Assert.Equal("daily_owner", report.DefinitionId);
        Assert.Equal("daily", report.Cadence);
        Assert.Equal(_start, report.PeriodStart);
        Assert.Equal(_end, report.PeriodEnd);
        Assert.Equal(ReportTestKit.Now, report.GeneratedAt);
        Assert.Equal("degraded", report.State);
        Assert.Equal(80, report.Score);
        Assert.Equal(ReportTestKit.Head, report.JournalHead);
        Assert.Equal(new ReportAlertCounts(1, 2, 3), report.Alerts);
        Assert.Equal(0, report.Omitted);
    }

    [Fact]
    public void A_frozen_report_carries_the_underlying_state()
    {
        HealthReport report = Build(ReportTestKit.Inputs(ReportTestKit.Snapshot("frozen", 40, "failing")));

        Assert.Equal("frozen", report.State);
        Assert.Equal("failing", report.UnderlyingState);
    }

    [Fact]
    public void Score_statistics_use_only_samples_inside_the_period()
    {
        HealthReport report = Build(ReportTestKit.Inputs(scores:
        [
            new ReportScorePoint(_start.AddHours(-1), 5),
            new ReportScorePoint(_start.AddHours(1), 90),
            new ReportScorePoint(_start.AddHours(2), 80),
            new ReportScorePoint(_end, 71),
            new ReportScorePoint(_end.AddHours(1), 1),
        ]));

        Assert.Equal(71, report.ScoreMin);
        Assert.Equal(90, report.ScoreMax);
        Assert.Equal(80, report.ScoreMean);
    }

    [Fact]
    public void The_mean_rounds_half_up()
    {
        HealthReport report = Build(ReportTestKit.Inputs(scores: [new ReportScorePoint(_start.AddHours(1), 10), new ReportScorePoint(_start.AddHours(2), 11)]));

        Assert.Equal(11, report.ScoreMean);
    }

    [Fact]
    public void No_samples_give_no_statistics()
    {
        HealthReport report = Build();

        Assert.Null(report.ScoreMin);
        Assert.Null(report.ScoreMax);
        Assert.Null(report.ScoreMean);
    }

    [Fact]
    public void State_changes_are_filtered_and_in_time_order()
    {
        HealthReport report = Build(ReportTestKit.Inputs(changes:
        [
            new ReportStateChange(_start.AddHours(5), KodoState.Healthy),
            new ReportStateChange(_start.AddHours(-3), KodoState.Failing),
            new ReportStateChange(_start.AddHours(2), KodoState.Degraded),
        ]));

        Assert.Equal(["degraded", "healthy"], report.StateChanges.Select(c => c.State));
        Assert.Equal(_start.AddHours(2), report.StateChanges[0].At);
    }

    [Fact]
    public void Only_components_that_are_not_green_are_listed_and_sorted()
    {
        HealthReport report = Build(ReportTestKit.Inputs(ReportTestKit.Snapshot(
            components:
            [
                new KodoComponentDto("server.ready", "green", null, _start),
                new KodoComponentDto("host.disk.free", "red", "disk_low", _start),
                new KodoComponentDto("data.backup", "amber", "backup_stale", _start),
            ])));

        Assert.Equal(["data.backup", "host.disk.free"], report.Components.Select(c => c.Id));
        Assert.Equal("disk_low", report.Components[1].Cause);
    }

    [Fact]
    public void Identifiers_and_causes_that_are_not_identifier_shaped_never_reach_the_report()
    {
        HealthReport report = Build(ReportTestKit.Inputs(
            ReportTestKit.Snapshot(components:
            [
                new KodoComponentDto("Agent Charlie's secret", "red", "x", _start),
                new KodoComponentDto("host.disk.free", "red", "the disk of alice@example.org is full", _start),
            ]),
            slos: [new ReportSlo("not an id", 995, 500)],
            incidents: [ReportIncidents.Incident("bad id!", _start.AddHours(1))]));

        Assert.Equal(3, report.Omitted);
        Assert.Equal("redacted", Assert.Single(report.Components).Cause);
        string json = Encoding.UTF8.GetString(HealthReportCodec.Encode(report));
        Assert.DoesNotContain("alice", json, StringComparison.Ordinal);
        Assert.DoesNotContain("secret", json, StringComparison.Ordinal);
    }

    [Fact]
    public void A_journal_head_that_is_not_a_hash_is_dropped_and_counted()
    {
        HealthReport report = Build(ReportTestKit.Inputs(ReportTestKit.Snapshot(head: "not-a-hash")));

        Assert.Null(report.JournalHead);
        Assert.Equal(1, report.Omitted);
    }

    [Fact]
    public void Lists_are_capped_and_the_rest_is_counted()
    {
        List<ReportStateChange> changes = [];
        for (int index = 0; index < HealthReportBuilder.MaxEntries + 5; index++)
        {
            changes.Add(new ReportStateChange(_start.AddMinutes(index), index % 2 == 0 ? KodoState.Degraded : KodoState.Healthy));
        }

        HealthReport report = Build(ReportTestKit.Inputs(changes: changes));

        Assert.Equal(HealthReportBuilder.MaxEntries, report.StateChanges.Count);
        Assert.Equal(5, report.Omitted);
    }

    [Fact]
    public void Incidents_are_kept_when_they_overlap_the_period()
    {
        HealthReport report = Build(ReportTestKit.Inputs(incidents:
        [
            ReportIncidents.Incident("inc-1", _start.AddDays(-3), _start.AddDays(-2)),
            ReportIncidents.Incident("inc-2", _start.AddDays(-1), _start.AddHours(1)),
            ReportIncidents.Incident("inc-3", _start.AddHours(3)),
            ReportIncidents.Incident("inc-4", _end.AddHours(1)),
        ]));

        Assert.Equal(["inc-2", "inc-3"], report.Incidents.Select(i => i.Id));
        Assert.Equal("critical", report.Incidents[0].Severity);
        Assert.Null(report.Incidents[1].ClosedAt);
    }

    [Fact]
    public void Slos_are_sorted_by_id()
    {
        HealthReport report = Build(ReportTestKit.Inputs(slos: [new ReportSlo("gc.append", 999, 400), new ReportSlo("server.ready", 995, 1000)]));

        Assert.Equal(["gc.append", "server.ready"], report.Slos.Select(s => s.Id));
        Assert.Equal(400, report.Slos[0].RemainingBudgetPermille);
    }

    [Fact]
    public void Structurally_wrong_inputs_are_refused()
    {
        ReportDefinition definition = ReportTestKit.Definition();

        Assert.Throws<ArgumentException>(() => HealthReportBuilder.Build(definition, _end, _start, ReportTestKit.Inputs(), ReportTestKit.Now));
        Assert.Throws<ArgumentException>(() => Build(ReportTestKit.Inputs(ReportTestKit.Snapshot(score: 101))));
        Assert.Throws<ArgumentException>(() => Build(ReportTestKit.Inputs(ReportTestKit.Snapshot(state: "sleepy"))));
        Assert.Throws<ArgumentException>(() => Build(ReportTestKit.Inputs(scores: [new ReportScorePoint(_start.AddHours(1), -1)])));
        Assert.Throws<ArgumentException>(() => Build(ReportTestKit.Inputs(slos: [new ReportSlo("a.b", 1001, 0)])));
        Assert.Throws<ArgumentException>(() => Build(ReportTestKit.Inputs(incidents: [ReportIncidents.Incident("inc-1", _start.AddHours(2), _start.AddHours(1))])));
    }

    [Fact]
    public void The_same_inputs_give_the_same_bytes_and_digest()
    {
        ReportInputs inputs = ReportTestKit.Inputs(scores: [new ReportScorePoint(_start.AddHours(1), 90)]);

        HealthReport first = Build(inputs);
        HealthReport second = Build(inputs);

        Assert.Equal(HealthReportCodec.Encode(first), HealthReportCodec.Encode(second));
        Assert.Equal(HealthReportCodec.Digest(first), HealthReportCodec.Digest(second));
        Assert.Equal(64, HealthReportCodec.Digest(first).Length);
    }

    [Fact]
    public void A_different_report_has_a_different_digest()
    {
        Assert.NotEqual(HealthReportCodec.Digest(Build()), HealthReportCodec.Digest(Build(ReportTestKit.Inputs(ReportTestKit.Snapshot(score: 79)))));
    }

    [Fact]
    public void A_report_round_trips_through_the_codec()
    {
        HealthReport report = Build(ReportTestKit.Inputs(
            slos: [new ReportSlo("server.ready", 995, 800)],
            incidents: [ReportIncidents.Incident("inc-3", _start.AddHours(3))]));

        HealthReport? decoded = HealthReportCodec.Decode(HealthReportCodec.Encode(report));

        Assert.NotNull(decoded);
        Assert.Equal(HealthReportCodec.Digest(report), HealthReportCodec.Digest(decoded));
    }

    [Fact]
    public void Damaged_or_foreign_bytes_decode_to_null()
    {
        Assert.Null(HealthReportCodec.Decode([]));
        Assert.Null(HealthReportCodec.Decode("{\"x\":1}"u8));
        Assert.Null(HealthReportCodec.Decode("not json"u8));
        Assert.Null(HealthReportCodec.Decode(new byte[HealthReportCodec.MaxBytes + 1]));
    }

}
