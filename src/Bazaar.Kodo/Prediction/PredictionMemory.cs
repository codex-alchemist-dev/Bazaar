namespace Bazaar.Kodo.Prediction;

/// <summary>
/// The saved state of one predictor, so it survives a restart (doc 46 13.2: models are stored so they can be replayed).
/// Times are UTC ticks. Every part is optional: a trend probe writes only <see cref="Trend"/>, a drift probe writes
/// <see cref="Baseline"/> and <see cref="Drift"/>. Restore validates everything and falls back to a fresh start.
/// </summary>
public sealed record PredictionMemory(int Version, TrendMemory? Trend, BaselineMemory? Baseline, DriftMemory? Drift)
{
    /// <summary>The current memory format version.</summary>
    public const int CurrentVersion = 1;
}

public sealed record TrendMemory(IReadOnlyList<SampleRecord> Samples);

public sealed record SampleRecord(long At, double Value);

public sealed record BaselineMemory(long? LastTicks, IReadOnlyList<BucketRecord> Buckets);

public sealed record BucketRecord(int Slot, IReadOnlyList<BucketEntryRecord> Entries);

public sealed record BucketEntryRecord(long Week, double Value);

public sealed record DriftMemory(double Positive, double Negative, long? PositiveStart, long? NegativeStart);
