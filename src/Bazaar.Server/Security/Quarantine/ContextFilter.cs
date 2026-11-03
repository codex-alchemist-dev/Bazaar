using Bazaar.Server.Security.Taint;

namespace Bazaar.Server.Security.Quarantine;

/// <summary>
/// Pure context filter. An item is delivered only when the taint delivery rule allows its tag and it is not
/// quarantined. A released item is delivered again, but its trust tag still drives taint, so lineage is kept.
/// </summary>
public sealed class ContextFilter : IContextFilter
{
    /// <inheritdoc />
    public IReadOnlyList<ContextCandidate<T>> Filter<T>(IEnumerable<ContextCandidate<T>> candidates)
    {
        ArgumentNullException.ThrowIfNull(candidates);
        return candidates.Where(c => IsDeliverable(c.Trust, c.Quarantine)).ToList();
    }

    /// <inheritdoc />
    public bool CanPromote(ContentTrust trust, QuarantineStatus? quarantine) => IsDeliverable(trust, quarantine);

    private static bool IsDeliverable(ContentTrust trust, QuarantineStatus? quarantine) =>
        TaintRules.IsDelivered(trust) && quarantine != QuarantineStatus.Quarantined;
}
