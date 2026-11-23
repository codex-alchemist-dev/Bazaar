namespace Bazaar.Server.Setup;

/// <summary>
/// A check answered by an <see cref="ISetupInfrastructureProbe"/> (doc 45 section 1.12). Healthy is green, degraded is amber and
/// failed is red. An unavailable probe, or no probe at all, is not-evaluated: the check never reports green without a reading.
/// </summary>
public sealed class ProbeBackedCheck(string id, string title, ISetupInfrastructureProbe? probe) : ISetupVerificationCheck
{
    private const string _noProbeReason = "needs the Host adapter";

    private readonly ISetupInfrastructureProbe? _probe = probe;

    /// <inheritdoc />
    public string Id { get; } = id;

    /// <inheritdoc />
    public string Title { get; } = title;

    /// <inheritdoc />
    public async Task<SetupCheckResult> RunAsync(SetupVerificationContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (_probe is null)
        {
            return SetupCheckResult.NotEvaluated(_noProbeReason);
        }

        var reading = await _probe.ProbeAsync(context, cancellationToken).ConfigureAwait(false);
        if (reading is null)
        {
            return SetupCheckResult.Red("The probe returned no reading.");
        }

        return reading.Outcome switch
        {
            SetupProbeOutcome.Healthy => SetupCheckResult.Green(reading.Message),
            SetupProbeOutcome.Degraded => SetupCheckResult.Amber(reading.Message, reading.Remediation),
            SetupProbeOutcome.Failed => SetupCheckResult.Red(reading.Message, reading.Remediation),
            _ => SetupCheckResult.NotEvaluated(reading.Message.TrimEnd('.')),
        };
    }
}
