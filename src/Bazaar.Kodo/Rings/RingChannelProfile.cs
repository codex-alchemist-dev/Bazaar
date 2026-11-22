namespace Bazaar.Kodo.Rings;

/// <summary>
/// How one channel runs its rings (doc 46 section 16.6): the halt thresholds, the probation a ring must stay live and healthy before the
/// next ring starts, and whether canary may be skipped. Nightly and beta halt more readily and have a shorter probation; LTS is slower
/// and never skips canary, whatever a setting says. Doc 49 owns the channel policy; these defaults are PROPOSED (section 16.9).
/// </summary>
/// <param name="Channel">The channel.</param>
/// <param name="Halt">The halt thresholds, validated against the floors.</param>
/// <param name="Probation">How long a ring must be live, with a healthy verdict, before the next ring may start.</param>
/// <param name="AllowSkipCanary">True when this channel may start a later ring with canary not cleared. Never true for LTS.</param>
public sealed record RingChannelProfile(ReleaseChannel Channel, RingHaltOptions Halt, TimeSpan Probation, bool AllowSkipCanary = false)
{
    /// <summary>The shortest probation a setting may take.</summary>
    public static TimeSpan MinProbation { get; } = TimeSpan.FromHours(1);

    /// <summary>The longest probation a setting may take.</summary>
    public static TimeSpan MaxProbation { get; } = TimeSpan.FromDays(30);

    /// <summary>The shipped profiles, one per channel.</summary>
    public static IReadOnlyDictionary<ReleaseChannel, RingChannelProfile> Defaults { get; } =
        new Dictionary<ReleaseChannel, RingChannelProfile>
        {
            [ReleaseChannel.Stable] = new(ReleaseChannel.Stable, new RingHaltOptions(), TimeSpan.FromHours(24)),
            [ReleaseChannel.Lts] = new(ReleaseChannel.Lts, new RingHaltOptions(), TimeSpan.FromHours(72)),
            [ReleaseChannel.Beta] = new(ReleaseChannel.Beta, Sensitive(), TimeSpan.FromHours(12)),
            [ReleaseChannel.Nightly] = new(ReleaseChannel.Nightly, Sensitive(), TimeSpan.FromHours(6)),
        };

    /// <summary>Checks the profile. A looser threshold, an out-of-range probation or an LTS that skips canary throws.</summary>
    public void Validate()
    {
        ArgumentNullException.ThrowIfNull(Halt);
        if (!Enum.IsDefined(Channel))
        {
            throw new ArgumentOutOfRangeException(nameof(Channel), Channel, "Unknown channel.");
        }

        Halt.Validate();
        ArgumentOutOfRangeException.ThrowIfLessThan(Probation, MinProbation);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(Probation, MaxProbation);
        if (Channel == ReleaseChannel.Lts && AllowSkipCanary)
        {
            throw new ArgumentException("LTS never skips canary.", nameof(AllowSkipCanary));
        }
    }

    private static RingHaltOptions Sensitive() => new()
    {
        CrashMultiple = 1.5m,
        CrashMinEvents = 10,
        ErrorMultiple = 1.5m,
        ErrorMinEvents = 25,
        StreamMultiple = 1.5m,
        StreamMinEvents = 10,
        SloBurnHalt = 4m,
        ObservationWindow = TimeSpan.FromMinutes(30),
    };
}
