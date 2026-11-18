namespace Bazaar.Kodo.Probes.Host;

/// <summary>
/// Warning and high thresholds for a used-percent reading. Amber at or past <see cref="Warn"/>, red at or past
/// <see cref="High"/>. Valid values satisfy 0 &lt; Warn &lt; High &lt;= 100.
/// </summary>
public sealed record UsedPercentThresholds
{
    public UsedPercentThresholds(double warn, double high)
    {
        if (!(warn > 0 && warn < high && high <= 100))
        {
            throw new ArgumentOutOfRangeException(nameof(warn), "Thresholds must satisfy 0 < warn < high <= 100.");
        }

        Warn = warn;
        High = high;
    }

    /// <summary>Used percent at which the probe turns amber.</summary>
    public double Warn { get; }

    /// <summary>Used percent at which the probe turns red.</summary>
    public double High { get; }
}
