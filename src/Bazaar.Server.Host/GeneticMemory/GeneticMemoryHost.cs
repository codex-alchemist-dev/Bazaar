using Bazaar.Server.Agents.Turns.Tools;
using Bazaar.Server.GeneticMemory;
using Bazaar.Server.GeneticMemory.Abstractions;
using Bazaar.Server.Http;
using Bazaar.Server.Identity;
using Bazaar.Server.Security.Taint;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Bazaar.Server.Host.GeneticMemory;

/// <summary>
/// Host glue for Genetic Memory Transfer (docs/design/25-company-systems-knowledge.md, PROPOSED review routes): the reviewer
/// resolver and the session lookup the module needs, and the route mapping behind the host's HTTP authentication. No feature logic.
/// </summary>
public static class GeneticMemoryHost
{
    /// <summary>The <see cref="HttpContext.Items"/> key the authentication filter stores the principal under.</summary>
    internal const string PrincipalItem = "Bazaar.GeneticMemory.Principal";

    /// <summary>Registers the owner-only reviewer resolver and the session lookup. Both use TryAdd, so a host can replace them.</summary>
    public static IServiceCollection AddBazaarGeneticMemoryHost(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.TryAddSingleton<IGmtReviewerResolver, OwnerOnlyReviewerResolver>();
        services.TryAddSingleton<IGmtChildContext, SessionChildContext>();
        return services;
    }

    /// <summary>Maps the review routes. Each runs the HTTP surface's authentication (transport, bearer, expiry, rate limit) first.</summary>
    public static IEndpointRouteBuilder MapBazaarGeneticMemoryHost(this IEndpointRouteBuilder routes) =>
        routes.MapBazaarGeneticMemory(route => route.AddEndpointFilter(async (context, next) =>
        {
            var (principal, failure) = await HttpSurfaceEndpoints.AuthenticateAsync(context.HttpContext);
            if (failure is not null)
            {
                return failure;
            }

            context.HttpContext.Items[PrincipalItem] = principal;
            return await next(context);
        }));
}

/// <summary>
/// Owner only, until the architect has a role in the principal model: the authenticated principal of kind <c>owner</c> reviews,
/// everyone else (agents, other humans, a missing principal) is refused. The role never comes from the request.
/// </summary>
public sealed class OwnerOnlyReviewerResolver : IGmtReviewerResolver
{
    /// <inheritdoc />
    public Task<GmtReviewer?> ResolveAsync(HttpContext http, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(http);
        var owner = http.Items.TryGetValue(GeneticMemoryHost.PrincipalItem, out var item)
            && item is Bazaar.Server.Wire.Connection.PrincipalRef { Kind: "owner" };
        return Task.FromResult<GmtReviewer?>(owner ? GmtReviewer.Owner : null);
    }
}

/// <summary>
/// Resolves a child's session for <c>gmt.propose_lesson</c>: the receiving agent's memory slug is its id (the same slug the memory
/// writer uses), the session is tainted when its recorded taint is soft or hard (the lesson then stays T1), and the agent is
/// quarantined when the state reader says so. Without a taint store the session is unknown and nothing is recorded (fail closed).
/// The names to keep out are empty: the host has no source of people or customer names, so only the secret screen and the reviewer apply.
/// </summary>
public sealed class SessionChildContext(IServiceProvider services) : IGmtChildContext
{
    /// <inheritdoc />
    public async Task<GmtChildContext?> ResolveAsync(Guid agentId, Guid sessionId, CancellationToken ct)
    {
        if (agentId == Guid.Empty || sessionId == Guid.Empty || services.GetService<ISessionTaintStore>() is not { } taint)
        {
            return null;
        }

        var state = await taint.LoadAsync(sessionId, ct).ConfigureAwait(false);
        var slug = agentId.ToString("D");
        var quarantined = services.GetService<IAgentStateReader>()?.IsQuarantined(slug) ?? false;
        return new GmtChildContext(slug, state.State != TaintState.Clean, quarantined, GmtTerms.None);
    }
}
