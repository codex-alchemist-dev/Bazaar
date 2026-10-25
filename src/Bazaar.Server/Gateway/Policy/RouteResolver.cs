using Bazaar.Contracts.Gateway;
using Bazaar.Server.Gateway.Data;

namespace Bazaar.Server.Gateway.Policy;

/// <summary>
/// Policy engine and route resolver (docs/design/05-model-gateway.md, work package 7). Security-critical.
/// Rules: deny by default; an unknown agent is denied; no wildcard or <c>auto</c> route resolves; a route is usable
/// only if the agent lists it, and its candidates are the members the agent also lists as models; a candidate with a
/// refused credential, a disabled provider, a missing model or a different model family is dropped; operator
/// tightening only removes candidates. The result is a fixed candidate list, so a fallback cannot reach a model that
/// was not allowed when the call was resolved.
/// </summary>
public sealed class RouteResolver(IAgentPolicyStore policies, IGatewayDirectory directory, PolicyTightening? tightening = null)
{
    /// <summary>The Skimmer gatekeeper scorer's policy subject (doc 05): a fixed model list and no tools.</summary>
    public const string GatekeeperSubject = "gatekeeper";

    private readonly PolicyTightening _tightening = tightening ?? PolicyTightening.None;

    public async ValueTask<RouteResolution> ResolveAsync(ModelRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (string.IsNullOrWhiteSpace(request.AgentId))
        {
            return Deny(DenyReason.UnknownAgent, "The request has no agent id.");
        }

        var policy = await policies.GetAsync(request.AgentId, cancellationToken).ConfigureAwait(false);
        if (policy is null)
        {
            return Deny(DenyReason.UnknownAgent, $"Agent '{request.AgentId}' has no model policy.");
        }

        if (request.AgentId == GatekeeperSubject && request.Tools.Count > 0)
        {
            return Deny(DenyReason.ToolsNotAllowed, "The gatekeeper subject may not use tools.");
        }

        var route = string.IsNullOrWhiteSpace(request.Route) ? policy.DefaultRoute : request.Route;
        if (route is null)
        {
            return Deny(DenyReason.NoRoute, "The request names no route and the policy has no default route.");
        }

        if (ModelRef.TryParse(route, out var single))
        {
            return await ResolveModelAsync(policy, route, single, cancellationToken).ConfigureAwait(false);
        }

        if (!RouteNames.IsValid(route))
        {
            return Deny(DenyReason.InvalidRoute, $"'{route}' is not a model id or a route name; wildcards and auto routes never resolve.");
        }

        return await ResolveRouteAsync(policy, route, cancellationToken).ConfigureAwait(false);
    }

    private async ValueTask<RouteResolution> ResolveModelAsync(AgentModelPolicy policy, string route, ModelRef model, CancellationToken ct)
    {
        if (!policy.AllowsModel(model))
        {
            return Deny(DenyReason.ModelNotAllowed, $"Agent '{policy.AgentId}' may not use '{model}'.");
        }

        return await BuildAsync(policy, route, RouteStrategy.Priority, [model], [], delegated: false, ct).ConfigureAwait(false);
    }

    private async ValueTask<RouteResolution> ResolveRouteAsync(AgentModelPolicy policy, string route, CancellationToken ct)
    {
        if (!policy.AllowsRoute(route))
        {
            return Deny(DenyReason.RouteNotAllowed, $"Agent '{policy.AgentId}' may not use route '{route}'.");
        }

        var stored = await directory.GetRouteAsync(route, ct).ConfigureAwait(false);
        if (stored is null)
        {
            return Deny(DenyReason.UnknownRoute, $"Route '{route}' does not exist.");
        }

        if (RouteNames.IsDelegated(route))
        {
            // The upstream picks the model, so the route is usable only if everything it is expected to serve is allowed.
            var expected = stored.ExpectedSet;
            if (expected.Count == 0 || expected.Any(e => !ExpectedEntryAllowed(policy, e)))
            {
                return Deny(DenyReason.DelegatedSetExceedsPolicy, $"Route '{route}' expects models outside agent '{policy.AgentId}' policy.");
            }

            return await BuildAsync(policy, route, stored.Strategy, stored.Members, expected, delegated: true, ct).ConfigureAwait(false);
        }

        var members = stored.Members.Where(policy.AllowsModel).ToList();
        if (members.Count == 0)
        {
            return Deny(DenyReason.NoEligibleCandidate, $"None of route '{route}' members is allowed for agent '{policy.AgentId}'.");
        }

        return await BuildAsync(policy, route, stored.Strategy, members, [], delegated: false, ct).ConfigureAwait(false);
    }

    private static bool ExpectedEntryAllowed(AgentModelPolicy policy, string entry) =>
        ModelRef.TryParse(entry, out var model)
            ? policy.AllowsModel(model)
            : policy.Models.Any(m => m.ModelId == entry);

    private async ValueTask<RouteResolution> BuildAsync(
        AgentModelPolicy policy,
        string route,
        RouteStrategy strategy,
        IReadOnlyList<ModelRef> members,
        IReadOnlyList<string> expected,
        bool delegated,
        CancellationToken ct)
    {
        var candidates = new List<RouteCandidate>();
        var dropped = new List<DroppedCandidate>();
        string? family = null;
        foreach (var member in members)
        {
            var reason = await CheckAsync(member, delegated, ct).ConfigureAwait(false);
            if (reason.Reason is { } denied)
            {
                dropped.Add(new DroppedCandidate(member, denied));
                continue;
            }

            if (family is not null && !string.Equals(family, reason.Family, StringComparison.Ordinal))
            {
                dropped.Add(new DroppedCandidate(member, DenyReason.FamilyMismatch));
                continue;
            }

            family ??= reason.Family;
            candidates.Add(new RouteCandidate(member, reason.Family!, delegated));
        }

        if (candidates.Count == 0)
        {
            var reason = dropped.Count > 0 && dropped.All(d => d.Reason == dropped[0].Reason) ? dropped[0].Reason : DenyReason.NoEligibleCandidate;
            return new RouteResolution.Denied(reason, $"No candidate for route '{route}' passed policy.", dropped);
        }

        return new RouteResolution.Allowed(new ResolvedRoute(policy.AgentId, route, strategy, policy.Version, candidates, expected), dropped);
    }

    private async ValueTask<(DenyReason? Reason, string? Family)> CheckAsync(ModelRef member, bool delegated, CancellationToken ct)
    {
        if (_tightening.Denies(member))
        {
            return (DenyReason.DeniedByTightening, null);
        }

        var provider = await directory.GetProviderAsync(member.ProviderId, ct).ConfigureAwait(false);
        if (provider is not { Enabled: true })
        {
            return (DenyReason.ProviderUnavailable, null);
        }

        if (!CredentialKinds.IsAcceptable(provider.AuthKind, provider.SecretRef))
        {
            return (DenyReason.CredentialRefused, null);
        }

        if (delegated)
        {
            return (null, "delegated");
        }

        var model = await directory.GetModelAsync(member, ct).ConfigureAwait(false);
        if (model is null || model.Status != GwModel.StatusAvailable)
        {
            return (DenyReason.ModelUnavailable, null);
        }

        return (null, model.Family);
    }

    private static RouteResolution.Denied Deny(DenyReason reason, string detail) => new(reason, detail, []);
}
