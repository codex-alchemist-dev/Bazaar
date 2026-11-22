using System.Text.RegularExpressions;
using Bazaar.Contracts.Kodo;
using Bazaar.Kodo.Api;
using Bazaar.Kodo.Evaluator;
using Bazaar.Kodo.Export;
using Bazaar.Kodo.Responder;

namespace Bazaar.Kodo.Reports;

/// <summary>
/// Builds a <see cref="HealthReport"/> from Kodo's own data (doc 46 section 16.7). Pure and deterministic: the same inputs and the same
/// generation time always give the same report, in a fixed order. It writes no free text. An identifier that does not match the
/// identifier shape is left out and counted in <see cref="HealthReport.Omitted"/>, never copied (the same fail-closed rule as alerts).
/// </summary>
public static partial class HealthReportBuilder
{
    /// <summary>The most entries in any one list of a report.</summary>
    public const int MaxEntries = 256;

    private const string _redactedCause = "redacted";

    /// <summary>Builds a report for the period ending at <paramref name="periodEnd"/>.</summary>
    /// <param name="definition">The report definition.</param>
    /// <param name="periodStart">The start of the period. Before <paramref name="periodEnd"/>.</param>
    /// <param name="periodEnd">The end of the period, normally the due instant.</param>
    /// <param name="inputs">The data to report.</param>
    /// <param name="generatedAt">When the report is built.</param>
    /// <exception cref="ArgumentException">The inputs are structurally wrong: a bad period, a score or permille out of range, or an incident that closes before it opens.</exception>
    public static HealthReport Build(
        ReportDefinition definition,
        DateTimeOffset periodStart,
        DateTimeOffset periodEnd,
        ReportInputs inputs,
        DateTimeOffset generatedAt)
    {
        ArgumentNullException.ThrowIfNull(definition);
        ArgumentNullException.ThrowIfNull(inputs);
        ArgumentNullException.ThrowIfNull(inputs.Snapshot);
        ArgumentNullException.ThrowIfNull(inputs.Snapshot.Status);
        ArgumentNullException.ThrowIfNull(inputs.StateChanges);
        ArgumentNullException.ThrowIfNull(inputs.Scores);
        ArgumentNullException.ThrowIfNull(inputs.Slos);
        ArgumentNullException.ThrowIfNull(inputs.Incidents);
        ArgumentNullException.ThrowIfNull(inputs.Alerts);

        DateTimeOffset start = periodStart.ToUniversalTime();
        DateTimeOffset end = periodEnd.ToUniversalTime();
        if (end <= start)
        {
            throw new ArgumentException("The period must end after it starts.", nameof(periodEnd));
        }

        KodoStatusDto status = inputs.Snapshot.Status;
        RequireScore(status.Score, "The snapshot score");
        RequireCount(inputs.Alerts.Info, inputs.Alerts.Warning, inputs.Alerts.Critical);

        int omitted = 0;

        List<ReportStateEntry> changes = [];
        foreach (ReportStateChange change in inputs.StateChanges.Where(c => c.At.ToUniversalTime() >= start && c.At.ToUniversalTime() <= end)
                     .OrderBy(c => c.At.ToUniversalTime()).ThenBy(c => (int)c.State))
        {
            if (changes.Count == MaxEntries)
            {
                omitted++;
                continue;
            }

            changes.Add(new ReportStateEntry(change.At.ToUniversalTime(), KodoWireNames.State(change.State)));
        }

        List<int> samples = [];
        foreach (ReportScorePoint point in inputs.Scores)
        {
            RequireScore(point.Score, "A score sample");
            DateTimeOffset at = point.At.ToUniversalTime();
            if (at >= start && at <= end)
            {
                samples.Add(point.Score);
            }
        }

        List<ReportComponentEntry> components = [];
        foreach (KodoComponentDto component in inputs.Snapshot.Components.Where(c => c.State != "green").OrderBy(c => c.Id, StringComparer.Ordinal))
        {
            if (!ValidId(component.Id) || components.Count == MaxEntries)
            {
                omitted++;
                continue;
            }

            string cause = component.Cause is not null && KodoExportPatterns.IdentifierValue().IsMatch(component.Cause) ? component.Cause : _redactedCause;
            components.Add(new ReportComponentEntry(component.Id, component.State, cause, component.Since.ToUniversalTime()));
        }

        List<ReportSloEntry> slos = [];
        foreach (ReportSlo slo in inputs.Slos.OrderBy(s => s.Id, StringComparer.Ordinal))
        {
            if (slo.TargetPermille is < 0 or > 1000 || slo.RemainingBudgetPermille is < 0 or > 1000)
            {
                throw new ArgumentException("An SLO figure must be from 0 to 1000 permille.", nameof(inputs));
            }

            if (!ValidId(slo.Id) || slos.Count == MaxEntries)
            {
                omitted++;
                continue;
            }

            slos.Add(new ReportSloEntry(slo.Id, slo.TargetPermille, slo.RemainingBudgetPermille));
        }

        List<ReportIncidentEntry> incidents = [];
        foreach (ReportIncident incident in inputs.Incidents.OrderBy(i => i.OpenedAt.ToUniversalTime()).ThenBy(i => i.Id, StringComparer.Ordinal))
        {
            DateTimeOffset opened = incident.OpenedAt.ToUniversalTime();
            DateTimeOffset? closed = incident.ClosedAt?.ToUniversalTime();
            if (closed < opened)
            {
                throw new ArgumentException("An incident must not close before it opens.", nameof(inputs));
            }

            if (opened > end || (closed is not null && closed < start))
            {
                continue;
            }

            if (incident.Id is null
                || !KodoExportPatterns.IdentifierValue().IsMatch(incident.Id)
                || !ValidId(incident.Category)
                || !Enum.IsDefined(incident.Severity)
                || incidents.Count == MaxEntries)
            {
                omitted++;
                continue;
            }

            incidents.Add(new ReportIncidentEntry(incident.Id, incident.Category, Word(incident.Severity), opened, closed));
        }

        int? min = null;
        int? max = null;
        int? mean = null;
        if (samples.Count > 0)
        {
            min = samples.Min();
            max = samples.Max();
            long sum = samples.Sum(sample => (long)sample);
            mean = (int)((2 * sum + samples.Count) / (2L * samples.Count));
        }

        string? head = inputs.Snapshot.JournalHead is not null && HexHash().IsMatch(inputs.Snapshot.JournalHead) ? inputs.Snapshot.JournalHead : null;
        if (inputs.Snapshot.JournalHead is not null && head is null)
        {
            omitted++;
        }

        string? underlying = status.UnderlyingState is not null && IsStateWord(status.UnderlyingState) ? status.UnderlyingState : null;
        if (!IsStateWord(status.State))
        {
            throw new ArgumentException("The snapshot state is not a Kodo state word.", nameof(inputs));
        }

        return new HealthReport(
            HealthReport.CurrentSchemaVersion,
            $"{definition.Id}-{end:yyyyMMddTHHmmssZ}",
            definition.Id,
            Word(definition.Schedule.Cadence),
            start,
            end,
            generatedAt.ToUniversalTime(),
            status.State,
            underlying,
            status.Score,
            min,
            max,
            mean,
            changes.AsReadOnly(),
            components.AsReadOnly(),
            slos.AsReadOnly(),
            incidents.AsReadOnly(),
            inputs.Alerts,
            head,
            omitted);
    }

    private static string Word<T>(T value)
        where T : struct, Enum => value.ToString().ToLowerInvariant();

    private static bool ValidId(string? id) => !string.IsNullOrEmpty(id) && KodoExportPatterns.ComponentId().IsMatch(id);

    private static bool IsStateWord(string word) => word is "healthy" or "degraded" or "failing" or "recovering" or "frozen";

    private static void RequireScore(int score, string what)
    {
        if (score is < 0 or > 100)
        {
            throw new ArgumentException($"{what} must be from 0 to 100.", nameof(score));
        }
    }

    private static void RequireCount(params int[] counts)
    {
        if (counts.Any(count => count < 0))
        {
            throw new ArgumentException("An alert count must not be negative.", nameof(counts));
        }
    }

    [GeneratedRegex("^[0-9a-f]{64}$", RegexOptions.CultureInvariant)]
    private static partial Regex HexHash();
}
