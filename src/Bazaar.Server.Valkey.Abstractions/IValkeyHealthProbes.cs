namespace Bazaar.Server.Valkey;

/// <summary>The four probes of the module health contract (docs/design/42-deps-as-modules.md section 11).</summary>
public enum ValkeyProbeKind
{
    /// <summary>The supervisor heartbeat: the managed child is running.</summary>
    Liveness,

    /// <summary>PING, and a set and get on a reserved key.</summary>
    Readiness,

    /// <summary>Memory under the warn threshold.</summary>
    Dependencies,

    /// <summary>A round trip on a reserved key, and for a managed server the eviction settings.</summary>
    SelfTest,
}

/// <summary>The outcome of one probe.</summary>
/// <param name="Kind">Which probe ran.</param>
/// <param name="Healthy">True when the probe passed, or does not apply (for example liveness of an external server).</param>
/// <param name="Critical">
/// Always false for Valkey. The cache is a cache: a failed probe makes the module amber and never opens a readiness gate
/// (docs/design/40 section 4b.2 and 4b.3, C07).
/// </param>
/// <param name="Reason">Why the probe failed, or null. Never contains the endpoint, the port or a secret.</param>
public sealed record ValkeyProbeResult(ValkeyProbeKind Kind, bool Healthy, bool Critical, string? Reason);

/// <summary>Runs the Valkey health probes. A failed server call is reported as an unhealthy result; caller cancellation is not swallowed.</summary>
public interface IValkeyHealthProbes
{
    /// <summary>Liveness of the managed child.</summary>
    Task<ValkeyProbeResult> LivenessAsync(CancellationToken ct);

    /// <summary>Readiness: PING and a reserved-key round trip.</summary>
    Task<ValkeyProbeResult> ReadinessAsync(CancellationToken ct);

    /// <summary>Memory use under the warn threshold.</summary>
    Task<ValkeyProbeResult> DependenciesAsync(CancellationToken ct);

    /// <summary>Round trip on a reserved key, plus the eviction check for a managed server.</summary>
    Task<ValkeyProbeResult> SelfTestAsync(CancellationToken ct);
}
