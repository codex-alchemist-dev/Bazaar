using System.Xml;
using Bazaar.Server.Knowledge.Metrics;

namespace Bazaar.Server.Health;

/// <summary>Turns one day's <see cref="HealthFacts"/> into metric samples through the pure collectors. No storage, no clock.</summary>
public static class HealthSampleBuilder
{
    public static IReadOnlyList<MetricSample> Build(string projectId, DateOnly day, HealthFacts facts)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projectId);
        ArgumentNullException.ThrowIfNull(facts);

        var samples = new List<MetricSample>();
        samples.AddRange(CiRunCollector.Collect(projectId, facts.CiRuns));

        foreach (var report in facts.Coverage)
        {
            try
            {
                if (CoverageCollector.Parse(projectId, report.Toolchain, day, report.CoberturaXml, report.SourceRef) is { } sample)
                    samples.Add(sample);
            }
            catch (XmlException)
            {
                // An unreadable report is a gap for the day, not a zero.
            }
        }

        foreach (var report in facts.Analyzer)
        {
            if (HealthCollectors.AnalyzerWarnings(projectId, report.Toolchain, day, report.Sarif, report.SourceRef) is { } sample)
                samples.Add(sample);
        }

        if (facts.PackageAudit is { } audit && HealthCollectors.PackageVulnerabilities(projectId, day, audit.Json, audit.SourceRef) is { } vuln)
            samples.Add(vuln);

        samples.AddRange(HealthCollectors.Reverts(projectId, facts.Reverts));
        samples.AddRange(HealthCollectors.ReopenedTasks(projectId, facts.Reopens));
        samples.AddRange(HealthCollectors.VoteReversals(projectId, facts.VoteReversals));

        if (facts.Board is { } board && HealthCollectors.BlockedShare(projectId, day, board.BlockedCards, board.TotalCards) is { } share)
            samples.Add(share);

        return samples;
    }
}
