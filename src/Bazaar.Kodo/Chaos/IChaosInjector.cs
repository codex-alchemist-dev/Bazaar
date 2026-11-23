namespace Bazaar.Kodo.Chaos;

/// <summary>
/// What an injector is asked to do. It names the audit digest of the allowing decision, so a fault cannot be started without one.
/// </summary>
/// <param name="RunId">An identifier for this run of the experiment.</param>
/// <param name="ExperimentId">The experiment.</param>
/// <param name="Fault">The fault.</param>
/// <param name="TargetId">The sandbox target (already classified by the gate).</param>
/// <param name="BlastRadiusPercent">The cap on how much it may touch.</param>
/// <param name="Duration">How long it may be held.</param>
/// <param name="AuditDigest">The digest of the <see cref="ChaosAuditRecord"/> that allowed it.</param>
public sealed record ChaosInjection(string RunId, string ExperimentId, ChaosFault Fault, string TargetId, int BlastRadiusPercent, TimeSpan Duration, string AuditDigest);

/// <summary>
/// Injects and reverts one kind of fault (doc 46 section 17.3). Interface only: the concrete faults (kill a module, slow a dependency,
/// drop frames, fill a disk, skew a clock, pause the gateway, corrupt one slot copy) need the doc 17 sandbox and are not built. An
/// implementation touches only the sandbox target it is given, never production data or keys, and <see cref="RevertAsync"/> must be
/// safe to call twice and must finish inside the abort deadline.
/// </summary>
public interface IChaosInjector
{
    /// <summary>The fault this injector handles. One injector per fault.</summary>
    ChaosFault Fault { get; }

    /// <summary>Starts the fault.</summary>
    Task InjectAsync(ChaosInjection injection, CancellationToken cancellationToken);

    /// <summary>Undoes the fault. Idempotent.</summary>
    Task RevertAsync(ChaosInjection injection, CancellationToken cancellationToken);
}
