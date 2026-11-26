namespace Bazaar.Server.Knowledge.Metrics;

/// <summary>
/// One derived measurement (doc 25 <c>metric_samples</c>). Identity is (project, metric, dimension, bucket, source_ref), so a
/// replayed source writes the same row again instead of a second one. Samples are never written for a missing source:
/// no CI run on a day means no sample for that day, which is a gap, not a zero.
/// </summary>
/// <param name="ProjectId">The project the sample belongs to.</param>
/// <param name="Metric">One of <see cref="MetricNames"/>.</param>
/// <param name="Dimension">Extra key, such as the toolchain for coverage. Empty when the metric has none.</param>
/// <param name="Bucket">The UTC day the sample covers.</param>
/// <param name="Value">The measurement.</param>
/// <param name="SourceRef">The fixed reference of the input, such as a CI run id or a report hash.</param>
public sealed record MetricSample(
    string ProjectId,
    string Metric,
    string Dimension,
    DateOnly Bucket,
    double Value,
    string SourceRef)
{
    /// <summary>The identity key used for upserts.</summary>
    public string Key => $"{ProjectId}\u001f{Metric}\u001f{Dimension}\u001f{Bucket:yyyy-MM-dd}\u001f{SourceRef}";
}

/// <summary>Metric names written by the collectors in this slice of doc 25 WP1.</summary>
public static class MetricNames
{
    /// <summary>Completed CI runs on the day. Count.</summary>
    public const string CiRunsTotal = "ci.runs_total";

    /// <summary>Completed CI runs that passed on the day. Count.</summary>
    public const string CiRunsPassed = "ci.runs_passed";

    /// <summary>Line coverage from a report, as a percentage. Dimension is the toolchain.</summary>
    public const string CoverageLinePercent = "coverage.line_percent";
}

/// <summary>A CI run as the collector receives it from the forge adapter (doc 21). Only completed runs are passed in.</summary>
/// <param name="RunId">Forge run id, used as the source reference.</param>
/// <param name="CompletedAt">When the run finished, UTC.</param>
/// <param name="Passed">True when the run concluded successfully.</param>
public sealed record CiRunFact(string RunId, DateTimeOffset CompletedAt, bool Passed);
