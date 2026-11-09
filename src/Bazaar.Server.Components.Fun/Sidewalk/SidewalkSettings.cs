namespace Bazaar.Server.Fun.Sidewalk;

/// <summary>
/// The Sidewalk settings from doc 26, section 8 (<c>sidewalk.*</c>), with the shipped defaults.
/// Sidewalk is the casual GC where social behaviour lives; the General Bazaar GC is the default
/// instance. No Kanban binding, no task routing, no reputation scoring.
/// </summary>
public sealed class SidewalkSettings
{
    /// <summary>
    /// <c>sidewalk.daily_token_cap</c>: the per-agent token budget drawn from the doc 15 quota table.
    /// Zero means the quota table value governs without a Sidewalk-specific cap.
    /// </summary>
    public int DailyTokenCap { get; init; }

    /// <summary>Throws when <see cref="DailyTokenCap"/> is negative.</summary>
    /// <exception cref="ArgumentOutOfRangeException">Cap is negative.</exception>
    public void Validate() => ArgumentOutOfRangeException.ThrowIfNegative(DailyTokenCap);
}
