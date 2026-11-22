using Bazaar.Core;
using Bazaar.Erasure;
using Bazaar.Fun;
using Bazaar.Sanitization;
using Bazaar.Server.Agents.Responder;
using Bazaar.Server.AgentSafety;
using Bazaar.Server.Blobs;
using Bazaar.Server.Gateway.Adapters.Anthropic;
using Bazaar.Server.Gateway.Adapters.Gemini;
using Bazaar.Server.Gateway.Adapters.OpenAi;
using Bazaar.Server.Gateway.AnthropicDirect;
using Bazaar.Server.Gateway.FreeLlmApi;
using Bazaar.Server.Host.Wire;
using Bazaar.Server.Host.Http;
using Bazaar.Server.Hosting;
using Bazaar.Server.Http;
using Bazaar.Server.Identity.Postgres;
using Bazaar.Server.Jobs;
using Bazaar.Server.Portability.Postgres;
using Bazaar.Server.Sanitization;
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
        services.AddBazaarPortabilityPostgres();
        services.AddBazaarLegalHoldReader();

        // The fun systems depend on the sanitizer abstractions only (Bazaar.Server.Sanitization.Abstractions). The host supplies the
        // implementation, and only when the fun-systems component is enabled, as before the split.
        if (BazaarComponentRegistration.IsEnabled(configuration, FunComponent.ManifestDefinition.Id))
        {
            services.TryAddSingleton<ITextSanitizer, TextSanitizer>();
        }

        // The fun systems depend on the sanitizer abstractions only (Bazaar.Server.Sanitization.Abstractions). The host supplies the
        // implementation, and only when the fun-systems component is enabled, as before the split.
        if (BazaarComponentRegistration.IsEnabled(configuration, FunComponent.ManifestDefinition.Id))
        {
            services.TryAddSingleton<ITextSanitizer, TextSanitizer>();
        }

        // Bazaar Components (docs/design/BAZAAR-COMPONENTS.md). Each entry is one component; the kernel orders and validates them.
        // Registered before the agent responder, so the GC component's hosted services (GC log, wire) still start before it.
        // The setup component shows its one-time bootstrap code through this; the setup modules themselves use no console.
        services.TryAddSingleton<Bazaar.Server.Setup.Host.IBootstrapCodeAnnouncer, ConsoleBootstrapCodeAnnouncer>();
        services.AddBazaarComponents(configuration, HostComponents.All);
        services.AddBazaarGcForkTarget();
        services.AddBazaarReplayWire();
        // After the components: the Valkey supervisor is one of them, and the deferred cache selection must start after it (doc 40 section 4c.4 item 8).
        services.AddBazaarCacheSelection();
        // Puts each packaged module's approved permission set in force once the components are up (doc 48 section 1).
        services.AddBazaarModuleHostWiring(HostComponents.All.Select(c => c.Manifest));
        // The Council's Vault decision writer (C24). It resolves the Vault writer on first use.
        services.AddBazaarCouncilDecisionVault();
        FreeLlmApiComponent.RequireComponentWhenGatewayIsOn(configuration);
        AnthropicDirectComponent.RequireComponentWhenGatewayIsOn(configuration);
        services.AddBazaarAgentResponder(configuration);
        // Pairing and refresh services (doc 32). Their routes are mapped by MapBazaarHttpSurface in Program.cs.
        services.AddBazaarRemoteAuth(configuration);
        services.AddBazaarBlobs(configuration);
        services.AddBazaarHttpSurface(configuration);
        services.AddBazaarCompanyReadOptions();
        return services;
    }
}
