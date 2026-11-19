namespace Bazaar.Kodo.Probes.Host;

/// <summary>
/// Warning and hard offsets for the clock. Amber when the absolute offset is at or past <see cref="Warn"/>, red
/// when it is at or past <see cref="Hard"/> (doc 46 section 9.1: critical past the hard limit).
/// Valid values satisfy <c>Zero &lt; Warn &lt; Hard</c>.
/// </summary>
public sealed record ClockDriftThresholds
{
    public ClockDriftThresholds(TimeSpan warn, TimeSpan hard)
    {
        if (warn <= TimeSpan.Zero || warn >= hard)
        {
            throw new ArgumentOutOfRangeException(nameof(warn), "Thresholds must satisfy 0 < warn < hard.");
        }

        Warn = warn;
        Hard = hard;
    }

    /// <summary>The absolute offset at which the probe turns amber.</summary>
    public TimeSpan Warn { get; }

    /// <summary>The absolute offset at which the probe turns red.</summary>
    public TimeSpan Hard { get; }
}
