using Bazaar.Kodo.Prediction;

namespace Bazaar.Kodo.Tests.Prediction;

internal static class PredictionTestData
{
    /// <summary>A fixed start that is not the epoch: Monday 2026-01-05 00:00 UTC.</summary>
    public static readonly DateTimeOffset Start = new(2026, 1, 5, 0, 0, 0, TimeSpan.Zero);

    public static List<TrendSample> Series(int count, TimeSpan step, Func<int, double> value) =>
        [.. Enumerable.Range(0, count).Select(i => new TrendSample(Start + (step * i), value(i)))];

    /// <summary>The time of the last sample of <see cref="Series"/>.</summary>
    public static DateTimeOffset EndOf(int count, TimeSpan step) => Start + (step * (count - 1));
}
