namespace Bazaar.Kodo.Standby;

/// <summary>
/// Timings for the standby controller. Every value is PROPOSED (doc 46 section 7.9). The primary stops at its lease expiry and the
/// standby waits for the expiry plus <see cref="ClockSkewMargin"/>, so the two never overlap while clocks differ by less than the margin.
/// </summary>
public sealed record StandbyOptions
{
    /// <summary>How long a lease lasts. Default 15 seconds.</summary>
    public TimeSpan LeaseDuration { get; init; } = TimeSpan.FromSeconds(15);

    /// <summary>The primary renews when this much or less of the lease is left. Default 5 seconds.</summary>
    public TimeSpan RenewBefore { get; init; } = TimeSpan.FromSeconds(5);

    /// <summary>Extra time past the expiry before a standby may take over. Default 3 seconds.</summary>
    public TimeSpan ClockSkewMargin { get; init; } = TimeSpan.FromSeconds(3);

    /// <summary>How long one witness call may take before it counts as no answer. Default 3 seconds.</summary>
    public TimeSpan WitnessTimeout { get; init; } = TimeSpan.FromSeconds(3);

    /// <summary>Throws when a value is out of range.</summary>
    /// <exception cref="ArgumentException">A timing is not positive, or the renew window or margin does not fit inside the lease.</exception>
    public void Validate()
    {
        if (LeaseDuration <= TimeSpan.Zero || WitnessTimeout <= TimeSpan.Zero || RenewBefore <= TimeSpan.Zero || ClockSkewMargin < TimeSpan.Zero)
        {
            throw new ArgumentException("Lease duration, renew window and witness timeout must be positive, and the skew margin not negative.");
        }

        if (RenewBefore >= LeaseDuration || ClockSkewMargin >= LeaseDuration)
        {
            throw new ArgumentException("The renew window and the skew margin must be shorter than the lease.");
        }
    }
}
