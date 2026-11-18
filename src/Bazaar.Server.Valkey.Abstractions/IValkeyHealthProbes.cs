namespace Bazaar.Server.Valkey;

/// <summary>The four probes of the Kodo module health contract (docs/design/42-deps-as-modules.md section 11).</summary>
public enum ValkeyProbeKind
{
    /// <summary>The supervisor heartbeat: the managed child is running.</summary>
    Liveness,

    /// <summary>PING, and a set and get on a reserved key.</summary>
    Readiness,

    /// <summary>Memory under the warn threshold.</summary>
    Dependencies,

    /// <summary>A round trip on a reserved key, removed afterwards.</summary>
    SelfTest,
}

/// <summary>The outcome of one probe.</summary>
/// <param name="Kind">Which probe ran.</param>
/// <param name="Healthy">True when the probe passed, or does not apply (for example liveness of an external server).</param>
/// <param name="Critical">True when a failure of this probe opens the readiness gate (doc 42 section 11). Only readiness is critical.</param>
/// <param name="Reason">Why the probe failed, or null. Never contains the endpoint or a secret.</param>
public sealed record ValkeyProbeResult(ValkeyProbeKind Kind, bool Healthy, bool Critical, string? Reason);

/// <summary>Runs the Valkey health probes. Probes never throw: a failed call is reported as an unhealthy result.</summary>
public interface IValkeyHealthProbes
{
    /// <summary>Liveness of the managed child.</summary>
    Task<ValkeyProbeResult> LivenessAsync(CancellationToken ct);

    /// <summary>Readiness: PING and a reserved-key round trip.</summary>
    Task<ValkeyProbeResult> ReadinessAsync(CancellationToken ct);

    /// <summary>Memory use under the warn threshold.</summary>
    Task<ValkeyProbeResult> DependenciesAsync(CancellationToken ct);

    /// <summary>Scheduled round trip on a reserved key.</summary>
    Task<ValkeyProbeResult> SelfTestAsync(CancellationToken ct);
}
