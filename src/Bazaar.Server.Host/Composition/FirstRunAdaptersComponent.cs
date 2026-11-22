using Bazaar.Core;
using Bazaar.Roles.Abstractions;
using Bazaar.Server.Agents.Data;
using Bazaar.Server.SoulProfile;
using Bazaar.Server.Souls;
using Bazaar.Server.Walkthrough;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Npgsql;

namespace Bazaar.Server.Host.Composition;

/// <summary>
/// Binds the profile route's ports to the modules that own the data (doc 07 read-only profile route, doc 45 section 2.9). It lives in
/// the composition root because it adapts Agents, Souls and Roles types to <c>Bazaar.Server.SoulProfile</c>, which references none of them.
/// Every registration is <c>TryAdd</c>, so a test or an operator's own implementation wins. The caller resolver is here too, over
/// <see cref="IBearerPersonLookup"/>; the lookup that checks a token (GET /auth/whoami, PR #1145) replaces the default, which knows no one.
/// </summary>
public sealed class FirstRunAdaptersComponent : IBazaarComponent
{
    public static ComponentManifest ManifestDefinition { get; } = new(
        Id: "first-run-adapters",
        Version: "0.1.0",
        Kind: ComponentKind.Feature,
        DependsOn: ["soul-profile"],
        Provides: []);

    public ComponentManifest Manifest => ManifestDefinition;

    public void AddServices(IServiceCollection services, IConfiguration configuration)
    {
        services.TryAddSingleton<IBearerPersonLookup, UnavailableBearerPersonLookup>();
        services.TryAddSingleton<BearerCallerResolver>(sp => new BearerCallerResolver(sp.GetRequiredService<IBearerPersonLookup>()));
        services.TryAddSingleton<IWalkthroughCallerResolver>(sp => sp.GetRequiredService<BearerCallerResolver>());
        services.TryAddSingleton<ISoulProfileCallerResolver>(sp => sp.GetRequiredService<BearerCallerResolver>());
        services.TryAddSingleton<ISoulVersionReader>(sp => new SoulVersioningVersionReader(
            new SoulVersioningService(sp.GetRequiredService<NpgsqlDataSource>())));
        services.TryAddSingleton<IAgentIdentityReader>(sp => new AgentRepositoryIdentityReader(sp.GetRequiredService<NpgsqlDataSource>()));
        services.TryAddSingleton<ISoulProfileAccess>(sp => new PermissionProfileAccess(sp.GetRequiredService<IPermissionChecker>()));
        services.TryAddSingleton<ISoulProfileSource>(sp => new SoulProfileSource(
            sp.GetRequiredService<ISoulProfileAccess>(),
            sp.GetRequiredService<IAgentIdentityReader>(),
            sp.GetRequiredService<ISoulBindingReader>(),
            sp.GetRequiredService<ISoulVersionReader>()));
    }
}

/// <summary>Reads a Soul's version chain from the Souls module and drops it to the metadata the profile route shows.</summary>
public sealed class SoulVersioningVersionReader(SoulVersioningService versions) : ISoulVersionReader
{
    public async Task<IReadOnlyList<SoulVersionInfo>> ListAsync(Guid soulId, CancellationToken cancellationToken = default) =>
        [.. (await versions.ListAsync(soulId, cancellationToken).ConfigureAwait(false)).Select(Map)];

    internal static SoulVersionInfo Map(Bazaar.Contracts.Soul.SoulVersion v) => new(
        v.Version, v.ParentVersion, v.CharterSha256, v.PreferencesJson, v.ContentSha256, v.Author, v.Reason, v.DecisionRef, v.CreatedAt);
}

/// <summary>
/// Reads the agent's display name from the agent row (doc 06). The row has no role or model column yet (doc 07 lists them under
/// Identity but the table does not store them), so those two fields are empty until the Agents module stores them.
/// </summary>
public sealed class AgentRepositoryIdentityReader(NpgsqlDataSource dataSource) : IAgentIdentityReader
{
    public async Task<AgentIdentity?> ReadAsync(Guid agentId, CancellationToken cancellationToken = default) =>
        await new AgentRepository(dataSource).GetAsync(agentId, cancellationToken).ConfigureAwait(false) is { } agent
            ? new AgentIdentity(agent.Name, "", "")
            : null;
}

/// <summary>
/// A person may read an agent's profile when they hold the <c>read</c> capability across the company (doc 48 section 4); the owner
/// role holds it. A person id that is not a human id, or a refused check, gives false, which the route shows as a plain 404.
/// </summary>
public sealed class PermissionProfileAccess(IPermissionChecker checker) : ISoulProfileAccess
{
    public async Task<bool> CanReadAsync(string personId, Guid agentId, CancellationToken cancellationToken = default) =>
        Guid.TryParse(personId, out var human)
        && await checker.IsAllowedAsync(human, PrincipalKind.Human, Capabilities.Read, Scope.Company, cancellationToken).ConfigureAwait(false);
}
