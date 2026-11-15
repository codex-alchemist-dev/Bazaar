namespace Bazaar.Server.Modules.Health;

/// <summary>How much work a self-test may do (doc 46 section 10.2).</summary>
public enum ModuleSelfTestScope
{
    /// <summary>Seconds, read-only.</summary>
    Quick,

    /// <summary>Runs inside a sandbox scope that is rolled back. Scheduled or on demand.</summary>
    Full,
}

/// <summary>The health status of one declared dependency of a module.</summary>
/// <param name="DependencyId">The id of the dependency module, as declared in the manifest.</param>
/// <param name="Check">The dependency's status as this module sees it.</param>
public sealed record ModuleDependencyHealth(string DependencyId, ModuleHealthCheck Check);

/// <summary>
/// The mandatory module health contract (D10, doc 42 section 11, doc 46 section 10). Every module, first-party or
/// third-party and dependencies included, implements the four probes. Liveness must not do I/O by rule.
/// Implementations must not throw from a probe; the <see cref="ModuleHealthProbeRunner"/> converts any escape anyway.
/// </summary>
public interface IModuleHealthContract
{
    /// <summary>The module's id, as in its manifest.</summary>
    string ModuleId { get; }

    /// <summary>Is the module's execution alive? No I/O. Budget 50 ms (doc 46).</summary>
    ValueTask<ModuleHealthCheck> LivenessAsync(CancellationToken cancellationToken);

    /// <summary>May the module take work now? May read cached dependency state. Budget 500 ms (doc 46).</summary>
    ValueTask<ModuleHealthCheck> ReadinessAsync(CancellationToken cancellationToken);

    /// <summary>The status of every dependency the module uses. Declared, not inferred.</summary>
    ValueTask<IReadOnlyList<ModuleDependencyHealth>> DependenciesAsync(CancellationToken cancellationToken);

    /// <summary>A bounded functional check, side-effect free or run in a sandbox scope that is rolled back.</summary>
    ValueTask<ModuleHealthCheck> SelfTestAsync(ModuleSelfTestScope scope, CancellationToken cancellationToken);
}
