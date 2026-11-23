using System.Collections.Concurrent;

namespace Bazaar.Server.Gateway.Selection;

/// <summary>
/// Binds routes to selection profiles and orders the gateway's already-checked candidates (doc 92). A route with no
/// binding gets <c>null</c>, which keeps the gateway's own order, so nothing changes until an operator or the wizard binds one.
/// The answer holds only endpoints that were passed in. Endpoints rejected for a breaker or quota are appended last, in
/// their original order, so the gateway's own skip logic still reports exact capacity facts; endpoints rejected for a
/// missing capability, a too-small context or an exclusion are omitted.
/// </summary>
public sealed class ProfileBoundRanker(EndpointSelector selector, SelectionInsights? insights = null) : ICandidateRanker, IAgentSelectionSetup
{
    private readonly ConcurrentDictionary<string, SelectionBinding> _bindings = new(StringComparer.Ordinal);

    public IReadOnlyList<Endpoint>? Order(string routeId, IReadOnlyList<Endpoint> candidates, SelectionRequest request)
    {
        ArgumentNullException.ThrowIfNull(routeId);
        ArgumentNullException.ThrowIfNull(candidates);
        ArgumentNullException.ThrowIfNull(request);
        if (!_bindings.TryGetValue(routeId, out var binding))
        {
            return null;
        }

        // Identity tiers are applied to copies, then mapped back, so the answer holds only the endpoints that were passed in.
        var byKey = candidates.GroupBy(c => c.Key).ToDictionary(g => g.Key, g => g.First());
        var scored = binding.IdentityTiers
            ? candidates.Select(c => c with { Tier = c.Tier + IdentityTier(c.Identity, binding.NativeFirst) }).ToList()
            : candidates;
        var decision = selector.Rank(scored, request with { Profile = request.Profile ?? binding.Profile });
        insights?.Record(routeId, decision);
        var ordered = decision.Ranking.Select(r => byKey[r.Endpoint.Key]).ToList();
        ordered.AddRange(decision.Rejected.Where(r => r.Reason is RejectionReason.BreakerOpen or RejectionReason.QuotaBlocked).Select(r => byKey[r.Endpoint.Key]));
        return ordered;
    }

    public Task<IReadOnlyList<string>> ListProfilesAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<string>>([.. selector.ProfileNames.Order(StringComparer.Ordinal)]);

    public Task BindAsync(string routeId, string profileName, CancellationToken cancellationToken = default) =>
        BindAsync(routeId, new SelectionBinding(profileName), cancellationToken);

    public Task BindAsync(string routeId, SelectionBinding binding, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(routeId);
        ArgumentNullException.ThrowIfNull(binding);
        if (!selector.ProfileNames.Contains(binding.Profile))
        {
            throw new ArgumentException($"Unknown selection profile '{binding.Profile}'.", nameof(binding));
        }

        _bindings[routeId] = binding;
        return Task.CompletedTask;
    }

    /// <summary>The profile a route is bound to, or null.</summary>
    public string? ProfileOf(string routeId) => _bindings.GetValueOrDefault(routeId)?.Profile;

    private static int IdentityTier(string identity, bool nativeFirst) => identity switch
    {
        "native" => nativeFirst ? 0 : 1,
        "pooled" => nativeFirst ? 1 : 0,
        _ => 0,
    };
}
