namespace Bazaar.Server.Components.Remote;

/// <summary>
/// Token lifetimes (docs/design/32-remote-access-auth-and-privacy.md, section 5 and Settings: <c>auth.access_token_minutes</c>,
/// <c>auth.refresh_sliding_days</c>, <c>auth.refresh_absolute_days</c>). The defaults are the shipped values. The settings
/// registry (doc 03) is not built yet, so the host binds these through options; a change is audited when it lands there.
/// </summary>
public sealed class RemoteTokenOptions
{
    /// <summary>Access token lifetime in minutes (<c>auth.access_token_minutes</c>).</summary>
    public int AccessTokenMinutes { get; set; } = 15;

    /// <summary>Refresh credential sliding lifetime in days, from each rotation (<c>auth.refresh_sliding_days</c>).</summary>
    public int RefreshSlidingDays { get; set; } = 30;

    /// <summary>Refresh credential lifetime cap in days, from the first credential of a device (<c>auth.refresh_absolute_days</c>).</summary>
    public int RefreshAbsoluteDays { get; set; } = 90;

    /// <summary>
    /// Longest lifetime accepted for any of the three values: 100 years. It is an arithmetic guard, not a policy: a larger
    /// value overflows the date arithmetic on every refresh instead of failing once at startup.
    /// </summary>
    private const int _maxDays = 36_500;

    /// <summary>Throws <see cref="ArgumentOutOfRangeException"/> when a value is not usable.</summary>
    public void Validate()
    {
        if (AccessTokenMinutes < 1)
            throw new ArgumentOutOfRangeException(nameof(AccessTokenMinutes), AccessTokenMinutes, "The access token lifetime must be at least one minute.");
        if (RefreshSlidingDays < 1)
            throw new ArgumentOutOfRangeException(nameof(RefreshSlidingDays), RefreshSlidingDays, "The refresh sliding lifetime must be at least one day.");
        if (AccessTokenMinutes > _maxDays * 24 * 60)
            throw new ArgumentOutOfRangeException(nameof(AccessTokenMinutes), AccessTokenMinutes, "The access token lifetime is longer than 100 years.");
        if (RefreshAbsoluteDays > _maxDays)
            throw new ArgumentOutOfRangeException(nameof(RefreshAbsoluteDays), RefreshAbsoluteDays, "The absolute refresh lifetime is longer than 100 years.");
        if (RefreshAbsoluteDays < RefreshSlidingDays)
            throw new ArgumentOutOfRangeException(nameof(RefreshAbsoluteDays), RefreshAbsoluteDays, "The absolute refresh lifetime cannot be shorter than the sliding lifetime.");
    }
}
