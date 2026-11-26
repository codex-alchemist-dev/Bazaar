using Bazaar.Core;
using Bazaar.Erasure;
using Bazaar.Install.Abstractions;
using Bazaar.Server.Agents.Responder;
using Bazaar.Server.AgentSafety;
using Bazaar.Server.Blobs;
using Bazaar.Server.CompanyBuild.State;
using Bazaar.Server.Gateway.Adapters.Anthropic;
using Bazaar.Server.Gateway.Adapters.Gemini;
using Bazaar.Server.Gateway.Adapters.OpenAi;
using Bazaar.Server.Gateway.AnthropicDirect;
using Bazaar.Server.Gateway.FreeLlmApi;
using Bazaar.Server.Hosting;
using Bazaar.Server.Http;
using Bazaar.Server.Identity.Postgres;
using Bazaar.Server.Jobs;
using Bazaar.Server.Souls.Retirement;
using Bazaar.Server.Wire.Connection;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Bazaar.Server.Host.Composition;

/// <summary>
/// The whole server service graph, in the order the host runs it. <c>Program.cs</c> calls this once; a test calls it to prove the
/// graph resolves, so a missing registration fails in CI instead of at the first start.
/// </summary>
public static class HostApplicationExtensions
{
    /// <summary>Registers every host module. Does not add the Windows service or systemd hooks, nor the status listener.</summary>
    public static IServiceCollection AddBazaarHostApplication(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddBazaarDevAuth(configuration);
        services.AddBazaarHostOptions(configuration);
        services.AddBazaarHostRuntime(configuration);
        services.AddBazaarRecoveryHook(configuration);
        services.AddBazaarSecrets();
        services.AddBazaarProviders(configuration);
        services.AddBazaarHostWire(configuration);
        services.AddBazaarSync(configuration);
        services.AddBazaarAgentSafety(configuration);
        services.AddBazaarPostgresCredentialLeases();
        services.AddBazaarVms(configuration);
        services.AddBazaarAgentSafetyModule(configuration);
        services.AddBazaarRetirementLedger();
        services.AddBazaarGateway();
        services.AddBazaarJobs();
        services.AddBazaarLegalHoldReader();

        // Bazaar Components (docs/design/BAZAAR-COMPONENTS.md). Each entry is one component; the kernel orders and validates them.
        // Registered before the agent responder, so the GC component's hosted services (GC log, wire) still start before it.
        services.AddBazaarComponents(configuration, HostComponents.All);
        // The module catalogue the panel and the company build state read. The build runner and the install-gate approval check
        // are seams: the executor is bound when Bazaar:CompanyBuild is configured (else a rebuild is refused with a reason), and the
        // approval requirement reads the install gate's rule (docs/design/51 section 5a).
        services.TryAddSingleton<IModuleCatalogue, HostModuleCatalogue>();
        services.AddBazaarCompanyBuildExecutor(configuration);
        services.TryAddSingleton<IModuleDeclarationSource, HostModuleDeclarationSource>();
        services.TryAddSingleton<IModuleApprovalRequirement>(sp => new InstallGateApprovalRequirement(
            sp.GetRequiredService<IModuleDeclarationSource>(), sp.GetService<IModulePermissionApprovalReader>()));
        // The Council's Vault decision writer (C24). It resolves the Vault writer on first use.
        services.AddBazaarCouncilDecisionVault();
        FreeLlmApiComponent.RequireComponentWhenGatewayIsOn(configuration);
        AnthropicDirectComponent.RequireComponentWhenGatewayIsOn(configuration);
        services.AddBazaarAgentResponder(configuration);
        // Pairing and refresh services (doc 32). Their routes are mapped by MapBazaarHttpSurface in Program.cs.
        services.AddBazaarRemoteAuth(configuration);
        services.AddBazaarBlobs(configuration);
        services.AddBazaarHttpSurface(configuration);
        return services;
    }
}
