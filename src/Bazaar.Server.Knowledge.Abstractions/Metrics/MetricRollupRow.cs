namespace Bazaar.Server.Knowledge.Metrics;

/// <summary>
/// One day-level rollup of a metric, recomputable from its samples. For counts the rollup is the sum; for rates it is the mean,
/// weighted by the number of contributing samples.
/// </summary>
public sealed record MetricRollupRow(string ProjectId, string Metric, string Dimension, DateOnly Bucket, double Value, int Samples);
