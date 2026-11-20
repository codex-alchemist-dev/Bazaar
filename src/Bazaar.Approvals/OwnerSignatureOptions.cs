namespace Bazaar.Approvals;

/// <summary>
/// Operator settings for owner-signature timing (doc 32 section 5a). Both values can only be tightened: a larger value is
/// clamped to its shipped ceiling, and a value below one second (lifetime) or below zero (skew) is clamped up.
/// </summary>
public sealed class OwnerSignatureOptions
{
    /// <summary>Shipped ceiling for <see cref="MaxLifetime"/>. Not a setting.</summary>
    public static readonly TimeSpan MaxLifetimeCeiling = TimeSpan.FromMinutes(15);

    /// <summary>Shipped ceiling for <see cref="ClockSkew"/>. Not a setting.</summary>
    public static readonly TimeSpan ClockSkewCeiling = TimeSpan.FromSeconds(60);

    private TimeSpan _maxLifetime = TimeSpan.FromMinutes(5);
    private TimeSpan _clockSkew = TimeSpan.FromSeconds(60);

    /// <summary>Longest allowed gap between IssuedAt and ExpiresAt. Default 5 minutes, ceiling 15 minutes.</summary>
    public TimeSpan MaxLifetime
    {
        get => _maxLifetime;
        set => _maxLifetime = Clamp(value, TimeSpan.FromSeconds(1), MaxLifetimeCeiling);
    }

    /// <summary>How far IssuedAt may be ahead of the server clock. Default 60 seconds, ceiling 60 seconds.</summary>
    public TimeSpan ClockSkew
    {
        get => _clockSkew;
        set => _clockSkew = Clamp(value, TimeSpan.Zero, ClockSkewCeiling);
    }

    private static TimeSpan Clamp(TimeSpan value, TimeSpan min, TimeSpan max) => value < min ? min : value > max ? max : value;
}
