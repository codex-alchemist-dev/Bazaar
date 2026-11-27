namespace Cdax.Core;

/// <summary>
/// What one failed component does to the rest of the graph (docs/design/BAZAAR-COMPONENTS.md section 4.1): hard dependents are
/// blocked (red readiness gate), soft dependents only lose the features that name the target (amber).
/// </summary>
/// <param name="Failed">The component that failed.</param>
/// <param name="Blocked">Every component that hard-depends on <see cref="Failed"/>, directly or through other hard edges, ordered by id.</param>
/// <param name="Degraded">Components that soft-depend on <see cref="Failed"/> or on a blocked component and are not themselves blocked, ordered by id.</param>
public sealed record ComponentImpact(string Failed, IReadOnlyList<string> Blocked, IReadOnlyList<string> Degraded);
