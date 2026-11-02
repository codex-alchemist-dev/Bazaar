using Bazaar.Server.Security.Taint;

namespace Bazaar.Server.Security.Quarantine;

/// <summary>Decides which candidate items may reach a context or a promotion (doc 15, "Quarantine").</summary>
public interface IContextFilter
{
    /// <summary>Returns the candidates that may be delivered, in their original order.</summary>
    IReadOnlyList<ContextCandidate<T>> Filter<T>(IEnumerable<ContextCandidate<T>> candidates);

    /// <summary>Whether an item may be promoted to a higher memory tier.</summary>
    bool CanPromote(ContentTrust trust, QuarantineStatus? quarantine);
}
