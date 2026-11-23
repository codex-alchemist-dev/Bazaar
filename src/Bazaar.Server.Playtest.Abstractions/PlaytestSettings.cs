namespace Bazaar.Server.Playtest.Abstractions;

/// <summary>
/// Playtest settings (doc 26 settings table, PROPOSED). Defaults are the safe shipped values; hosted vision is off unless an
/// operator turns it on.
/// </summary>
public sealed record PlaytestSettings
{
    public const int MinCaptureIntervalSeconds = 1;
    public const int MaxCaptureIntervalSeconds = 3600;

    /// <summary><c>playtest.capture_interval_seconds</c>; used by Tier B capture, validated here so the setting is one value.</summary>
    public int CaptureIntervalSeconds { get; init; } = 60;

    /// <summary><c>playtest.hosted_vision</c>; default false.</summary>
    public bool HostedVision { get; init; }

    /// <summary>Returns this instance, or throws <see cref="ArgumentOutOfRangeException"/> when a value is outside its allowed range.</summary>
    public PlaytestSettings Validated()
    {
        if (CaptureIntervalSeconds is < MinCaptureIntervalSeconds or > MaxCaptureIntervalSeconds)
        {
            throw new ArgumentOutOfRangeException(
                nameof(CaptureIntervalSeconds), CaptureIntervalSeconds,
                $"playtest.capture_interval_seconds must be {MinCaptureIntervalSeconds} to {MaxCaptureIntervalSeconds}.");
        }

        return this;
    }
}
