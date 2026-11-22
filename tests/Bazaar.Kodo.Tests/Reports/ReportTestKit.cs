using Bazaar.Contracts.Kodo;
using Bazaar.Kodo.Evaluator;
using Bazaar.Kodo.Journal;
using Bazaar.Kodo.Reports;
using Bazaar.Kodo.Responder;

namespace Bazaar.Kodo.Tests.Reports;

/// <summary>Fixtures and fakes for the report tests.</summary>
internal static class ReportTestKit
{
    public static readonly DateTimeOffset Now = new(2026, 11, 22, 12, 0, 0, TimeSpan.Zero);

    public static DateTimeOffset Utc(int year, int month, int day, int hour = 0) => new(year, month, day, hour, 0, 0, TimeSpan.Zero);

    public static ReportDefinition Definition(string id = "daily_owner") =>
        new(id, ReportSchedule.Fixed(ReportCadence.Daily, TimeSpan.FromHours(6), TimeSpan.Zero));

    public static string Head => new('a', 64);

    public static KodoStatusSnapshotDto Snapshot(string state = "degraded", int score = 80, string? underlying = null, string? head = null, params KodoComponentDto[] components) =>
        new(
            KodoStatusSnapshotDto.CurrentSchemaVersion,
            7,
            new KodoStatusDto(state, underlying, score, Now, []),
            head ?? Head,
            components);

    public static ReportInputs Inputs(
        KodoStatusSnapshotDto? snapshot = null,
        IReadOnlyList<ReportStateChange>? changes = null,
        IReadOnlyList<ReportScorePoint>? scores = null,
        IReadOnlyList<ReportSlo>? slos = null,
        IReadOnlyList<ReportIncident>? incidents = null) =>
        new(
            snapshot ?? Snapshot(),
            changes ?? [],
            scores ?? [],
            slos ?? [],
            incidents ?? [],
            new ReportAlertCounts(1, 2, 3));
}

internal sealed class FakeInputSource : IReportInputSource
{
    public Func<DateTimeOffset, ValueTask<ReportInputs>> Handler { get; set; } = _ => ValueTask.FromResult(ReportTestKit.Inputs());

    public List<(DateTimeOffset Start, DateTimeOffset End)> Calls { get; } = [];

    public ValueTask<ReportInputs> GetAsync(ReportDefinition definition, DateTimeOffset periodStart, DateTimeOffset periodEnd, CancellationToken cancellationToken)
    {
        Calls.Add((periodStart, periodEnd));
        return Handler(periodEnd);
    }
}

internal sealed class FakeSink(string id, Func<HealthReport, CancellationToken, ValueTask<bool>>? handler = null) : IReportSink
{
    public string Id { get; } = id;

    public List<HealthReport> Received { get; } = [];

    public ValueTask<bool> PublishAsync(HealthReport report, CancellationToken cancellationToken)
    {
        Received.Add(report);
        return handler is null ? ValueTask.FromResult(true) : handler(report, cancellationToken);
    }
}

internal sealed class ThrowingIdSink : IReportSink
{
    public string Id => throw new InvalidOperationException("no id");

    public ValueTask<bool> PublishAsync(HealthReport report, CancellationToken cancellationToken) => ValueTask.FromResult(true);
}

internal sealed class MemoryJournal : IReportJournal
{
    public List<JournalRecord> Records { get; } = [];

    public bool Fail { get; set; }

    public ValueTask AppendAsync(JournalRecord record, CancellationToken cancellationToken)
    {
        if (Fail)
        {
            throw new IOException("journal is full");
        }

        Records.Add(record);
        return ValueTask.CompletedTask;
    }
}

internal static class ReportIncidents
{
    public static ReportIncident Incident(string id, DateTimeOffset opened, DateTimeOffset? closed = null, string category = "host") =>
        new(id, category, AlertSeverity.Critical, opened, closed);
}
