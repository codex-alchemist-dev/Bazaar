using Bazaar.Server.Gateway.Data;
using Bazaar.Server.Skimmer;

namespace Bazaar.Server.Host.Composition;

/// <summary>
/// Adapts <see cref="IGatekeeperPolicySeed"/> onto <c>agent_model_policy</c> (doc 05, "Policy"). The <c>gatekeeper</c> subject gets
/// exactly one model and no wildcard. A row an operator edited (any other <c>changed_by</c>) is never overwritten.
/// </summary>
public sealed class GatekeeperPolicySeedAdapter(AgentModelPolicyRepository policies) : IGatekeeperPolicySeed
{
    /// <summary>The changed-by label on the seeded row.</summary>
    public const string ChangedBy = "skimmer-seed";

    public async Task<bool> EnsureAsync(string model, CancellationToken ct)
    {
        var existing = await policies.GetRowAsync(SkimmerComponent.GatekeeperSubject, ct).ConfigureAwait(false);
        if (existing is not null && !string.Equals(existing.ChangedBy, ChangedBy, StringComparison.Ordinal))
        {
            return false;
        }

        await policies.SaveAsync(SkimmerComponent.GatekeeperSubject, [model], model, ChangedBy, ct).ConfigureAwait(false);
        return true;
    }
}
