using Bazaar.Kodo.Probes;
using Bazaar.Kodo.Probes.Host;

namespace Bazaar.Server.Setup;

/// <summary>
/// Maps a Kodo probe result to a setup check result (doc 45 section 1.12, doc 46 section 9). Green is green, amber is amber and red
/// is red. Unknown (an unreadable source, a timeout) is not-evaluated, never green. The cause is Kodo's own cause string.
/// </summary>
internal static class KodoProbeMapping
{
    public static SetupCheckResult ToCheckResult(ProbeResult result, string subject, string? remediation)
    {
        ArgumentNullException.ThrowIfNull(result);
        return result.Status switch
        {
            ProbeStatus.Green => SetupCheckResult.Green($"{subject} is within its limits."),
            ProbeStatus.Amber => SetupCheckResult.Amber($"{subject} is past its warning limit ({result.Cause}).", remediation),
            ProbeStatus.Red => SetupCheckResult.Red($"{subject} is past its hard limit ({result.Cause}).", remediation),
            _ => SetupCheckResult.NotEvaluated($"{subject} could not be read ({result.Cause})"),
        };
    }
}

/// <summary>
/// <c>host.disk</c>: runs Kodo's <see cref="DiskFreeProbe"/> on each volume and reports the worst result (doc 45 section 1.12).
/// Read-only. The thresholds are Kodo's (warn 80 percent used, high 95 percent, the latter PROPOSED in doc 46).
/// </summary>
public sealed class KodoDiskHeadroomCheck : ISetupVerificationCheck
{
    private readonly IHostReadings _readings;
    private readonly List<string> _volumes;

    public KodoDiskHeadroomCheck(IHostReadings readings, IEnumerable<string> volumes)
    {
        ArgumentNullException.ThrowIfNull(readings);
        ArgumentNullException.ThrowIfNull(volumes);
        _readings = readings;
        _volumes = [.. volumes];
        if (_volumes.Count == 0)
        {
            throw new ArgumentException("At least one volume is needed.", nameof(volumes));
        }
    }

    /// <inheritdoc />
    public string Id => "host.disk";

    /// <inheritdoc />
    public string Title => "Disk headroom";

    /// <inheritdoc />
    public async Task<SetupCheckResult> RunAsync(SetupVerificationContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        SetupCheckResult? worst = null;
        foreach (var volume in _volumes)
        {
            var probe = new DiskFreeProbe(_readings, volume);
            var result = KodoProbeMapping.ToCheckResult(
                await probe.RunAsync(cancellationToken).ConfigureAwait(false),
                "Disk usage on a data volume",
                "Free space on the volume, or move the data root to a larger one.");
            if (worst is null || Rank(result.Status) > Rank(worst.Status))
            {
                worst = result;
            }
        }

        return worst!;
    }

    private static int Rank(SetupCheckStatus status) => status switch
    {
        SetupCheckStatus.Red => 3,
        SetupCheckStatus.NotEvaluated => 2,
        SetupCheckStatus.Amber => 1,
        _ => 0,
    };
}

/// <summary><c>host.clock</c>: runs Kodo's <see cref="ClockDriftProbe"/> (doc 45 section 1.12). Read-only.</summary>
public sealed class KodoClockSyncCheck(IHostReadings readings) : ISetupVerificationCheck
{
    private readonly IHostReadings _readings = readings ?? throw new ArgumentNullException(nameof(readings));

    /// <inheritdoc />
    public string Id => "host.clock";

    /// <inheritdoc />
    public string Title => "Clock sync";

    /// <inheritdoc />
    public async Task<SetupCheckResult> RunAsync(SetupVerificationContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        var result = await new ClockDriftProbe(_readings).RunAsync(cancellationToken).ConfigureAwait(false);
        return KodoProbeMapping.ToCheckResult(result, "The local clock offset", "Enable time sync on the host.");
    }
}
