namespace Bazaar.Server.OfflineSync;

/// <summary>Operator settings for the offline sync engine (doc 56 section 3).</summary>
public sealed class OfflineSyncOptions
{
    /// <summary>The smallest allowed <see cref="MaxClockSkew"/>.</summary>
    public static readonly TimeSpan MinimumClockSkew = TimeSpan.FromMinutes(1);

    private TimeSpan _maxClockSkew = TimeSpan.FromHours(24);

    /// <summary>How far ahead of the server clock a write's wall time may be before it is held for review. Default 24 hours; floor 1 minute.</summary>
    /// <exception cref="ArgumentOutOfRangeException">The value is below <see cref="MinimumClockSkew"/>.</exception>
    public TimeSpan MaxClockSkew
    {
        get => _maxClockSkew;
        set
        {
            ArgumentOutOfRangeException.ThrowIfLessThan(value, MinimumClockSkew);
            _maxClockSkew = value;
        }
    }
}
