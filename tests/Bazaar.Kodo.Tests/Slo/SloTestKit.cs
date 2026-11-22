using Bazaar.Kodo.Slo;

namespace Bazaar.Kodo.Tests.Slo;

/// <summary>Fixtures for the SLO tests. Time is a fixed instant on a slot boundary, so slot arithmetic is exact.</summary>
internal static class SloTestKit
{
    public static readonly DateTimeOffset Now = new(2026, 11, 22, 12, 0, 0, TimeSpan.Zero);

    public static readonly TimeSpan Minute = TimeSpan.FromMinutes(1);

    /// <summary>Records one sample inside the slot that ended <paramref name="minutesAgo"/> minutes before <see cref="Now"/> (1 is the last complete slot).</summary>
    public static void Put(ISloSampleStore store, string id, int minutesAgo, SloSampleOutcome outcome, int times = 1)
    {
        for (int i = 0; i < times; i++)
        {
            Assert.True(store.Record(id, Now - (minutesAgo * Minute) + TimeSpan.FromSeconds(10), outcome));
        }
    }

    /// <summary>Records one sample in each slot from <paramref name="fromMinutesAgo"/> down to <paramref name="toMinutesAgo"/>, both included.</summary>
    public static void PutRange(ISloSampleStore store, string id, int fromMinutesAgo, int toMinutesAgo, SloSampleOutcome outcome)
    {
        for (int m = fromMinutesAgo; m >= toMinutesAgo; m--)
        {
            Put(store, id, m, outcome);
        }
    }

    public static SloDefinition Availability(int targetPpm = 950_000, TimeSpan? window = null) =>
        new("svc", targetPpm, window ?? TimeSpan.FromHours(1), SloIndicatorKind.Availability);

    public static SloDefinition Latency(int targetPpm = 950_000) =>
        new("lat", targetPpm, TimeSpan.FromHours(1), SloIndicatorKind.LatencyThreshold, TimeSpan.FromMilliseconds(250));
}
