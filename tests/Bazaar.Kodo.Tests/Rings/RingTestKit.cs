using Bazaar.Kodo.Rings;

namespace Bazaar.Kodo.Tests.Rings;

/// <summary>Fixtures for the ring tests.</summary>
internal static class RingTestKit
{
    public static readonly DateTimeOffset Start = new(2026, 11, 22, 8, 0, 0, TimeSpan.Zero);

    /// <summary>A healthy, well-sampled cohort over a full window. Override fields with <c>with</c>.</summary>
    public static CohortSignals Cohort(string release = "v0.2.0", ReleaseRing ring = ReleaseRing.Canary, DateTimeOffset? start = null) =>
        new(release, ring, start ?? Start, (start ?? Start).AddMinutes(60), Devices: 10, Sessions: 2000, Crashes: 2, ErrorEvents: 10,
            StreamAttempts: 2000, StreamFailures: 4, [], 1m, 5, 0, []);

    public static CohortSignals Baseline(string release = "v0.1.0") => Cohort(release) with { Crashes = 4 };

    public static ReleaseRollout LiveCanary(ReleaseChannel channel = ReleaseChannel.Stable)
    {
        ReleaseRollout rollout = ReleaseRollout.Begin("v0.2.0", channel);
        return RingRollouts.Start(rollout, ReleaseRing.Canary, RingChannelProfile.Defaults[channel], Start).Rollout;
    }
}
