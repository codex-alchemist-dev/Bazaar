using System.Collections.Concurrent;

namespace Bazaar.Server.Gateway.Selection;

/// <summary>
/// Binds routes to selection profiles and orders the gateway's already-checked candidates (doc 92). A route with no
/// binding gets <c>null</c>, which keeps the gateway's own order, so nothing changes until an operator or the wizard binds one.
/// The answer holds only endpoints that were passed in. Endpoints rejected for a breaker or quota are appended last, in
/// their original order, so the gateway's own skip logic still reports exact capacity facts; endpoints rejected for a
/// missing capability, a too-small context or an exclusion are omitted.
/// </summary>
public sealed class ProfileBoundRanker(EndpointSelector selector) : ICandidateRanker, IAgentSelectionSetup
{
    private readonly ConcurrentDictionary<string, string> _bindings = new(StringComparer.Ordinal);

    public IReadOnlyList<Endpoint>? Order(string routeId, IReadOnlyList<Endpoint> candidates, SelectionRequest request)
    {
        ArgumentNullException.ThrowIfNull(routeId);
        ArgumentNullException.ThrowIfNull(candidates);
        ArgumentNullException.ThrowIfNull(request);
        if (!_bindings.TryGetValue(routeId, out var profile))
        {
            return null;
        }

        var decision = selector.Rank(candidates, request with { Profile = request.Profile ?? profile });
        var ordered = decision.Ranking.Select(r => r.Endpoint).ToList();
        ordered.AddRange(decision.Rejected.Where(r => r.Reason is RejectionReason.BreakerOpen or RejectionReason.QuotaBlocked).Select(r => r.Endpoint));
        return ordered;
    }

    public Task<IReadOnlyList<string>> ListProfilesAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<string>>([.. selector.ProfileNames.Order(StringComparer.Ordinal)]);

    public Task BindAsync(string routeId, string profileName, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(routeId);
        if (!selector.ProfileNames.Contains(profileName))
        {
            throw new ArgumentException($"Unknown selection profile '{profileName}'.", nameof(profileName));
        }

        _bindings[routeId] = profileName;
        return Task.CompletedTask;
    }

    /// <summary>The profile a route is bound to, or null.</summary>
    public string? ProfileOf(string routeId) => _bindings.GetValueOrDefault(routeId);
}
