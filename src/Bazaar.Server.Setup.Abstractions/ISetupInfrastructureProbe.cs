namespace Bazaar.Server.Setup;

/// <summary>What a service probe found. <see cref="Unavailable"/> means the probe could not look, which is never green.</summary>
public enum SetupProbeOutcome
{
    Healthy,
    Degraded,
    Failed,
    Unavailable,
}

/// <summary>
/// One probe reading. The message names the check and the rule and never carries a secret value or the text of a secret reference.
/// </summary>
public sealed record SetupProbeReading(SetupProbeOutcome Outcome, string Message, string? Remediation = null);

/// <summary>
/// A read-only probe of one real service for one verification check (doc 45 section 1.12), supplied by the host adapter: for example
/// the schema state, the PostgreSQL connection or the backup target. The setup module has no Kodo surface for these services yet,
/// so this is the seam: the Host registers a probe per check id and the check falls back to "not evaluated" when none is present.
/// A probe never writes and never returns a secret value.
/// </summary>
public interface ISetupInfrastructureProbe
{
    /// <summary>The id of the verification check this probe answers, for example <c>database.postgres</c>.</summary>
    string CheckId { get; }

    /// <summary>Looks at the service. An exception becomes a red result for this check only.</summary>
    Task<SetupProbeReading> ProbeAsync(SetupVerificationContext context, CancellationToken cancellationToken);
}
