using Bazaar.Contracts.Wire.Codec;
using Bazaar.Server.Agents.LoopGuard;
using Bazaar.Server.Gateway.Usage;
using Bazaar.Server.Host.Startup;
using Bazaar.Server.Host.Status;
using Bazaar.Server.Providers;
using Bazaar.Server.Providers.Linux;
using Bazaar.Server.Providers.Windows;
using Bazaar.Server.Secrets;
using Bazaar.Server.Security.Taint;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Bazaar.Server.Host.Composition;

/// <summary>One <c>AddXxx</c> per module. <c>Program.cs</c> only calls these; HOST1 is the only place modules are wired.</summary>
public static class HostCompositionExtensions
{
    /// <summary>Startup sequence, instance lock, readiness checks and the status listener options (doc 03 steps 3 to 7, WP7).</summary>
    public static IServiceCollection AddBazaarHostRuntime(this IServiceCollection services, IConfiguration configuration)
    {
        services.TryAddSingleton(TimeProvider.System);
        services.AddOptions<BazaarStatusOptions>().Bind(configuration.GetSection(BazaarStatusOptions.SectionName)).ValidateOnStart();
        services.AddSingleton<IValidateOptions<BazaarStatusOptions>, BazaarStatusOptionsValidator>();
        services.AddOptions<BazaarReadinessOptions>().Bind(configuration.GetSection(BazaarReadinessOptions.SectionName));
        services.TryAddSingleton<HostState>();
        services.TryAddSingleton<HostExitCode>();
        services.TryAddSingleton<DatabaseConnection>();
        services.TryAddSingleton<IInstanceLock, PostgresInstanceLock>();
        services.TryAddSingleton<ISchemaGate, MigrationSchemaGate>();
        services.AddSingleton<IReadinessCheck, DatabaseReadinessCheck>();
        services.AddSingleton<IReadinessCheck, InstanceLockReadinessCheck>();
        services.AddSingleton<IReadinessCheck, ProviderReadinessCheck>();
        services.AddHostedService<StartupSequence>();
        return services;
    }

    /// <summary>Bootstrap secret references: <c>env:</c> and <c>file:</c> everywhere, plus the OS resolver (<c>dpapi:</c> or <c>systemd-creds:</c>).</summary>
    public static IServiceCollection AddBazaarSecrets(this IServiceCollection services)
    {
        services.AddSingleton<ISecretResolver>(_ =>
        {
            var resolvers = new List<ISecretResolver> { new BootstrapSecretResolver() };
            if (OperatingSystem.IsWindows())
            {
                resolvers.Add(new DpapiSecretResolver());
            }
            else if (OperatingSystem.IsLinux())
            {
                resolvers.Add(new SystemdCredsSecretResolver());
            }

            return new CompositeSecretResolver(resolvers);
        });
        return services;
    }

    /// <summary>
    /// Picks one provider per role at start (doc 03). Only the service host has OS implementations so far; the other roles default to
    /// <c>none</c> until their providers land. Explicit choices that are not usable fail the start with no silent fallback.
    /// </summary>
    public static IServiceCollection AddBazaarProviders(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddOptions<BazaarProviderOptions>().Bind(configuration.GetSection(BazaarProviderOptions.SectionName));
        services.TryAddSingleton(sp =>
        {
            var options = sp.GetRequiredService<IOptions<BazaarProviderOptions>>().Value;
            IReadOnlyList<IServiceHostProvider> serviceHosts = [.. WindowsProviders.ServiceHostCandidates(), .. LinuxProviders.ServiceHostCandidates()];
            return new ProviderCapabilityReport(
            [
                ProviderSelector.Select(ProviderRole.ServiceHost, options.ServiceHost, serviceHosts),
                ProviderSelector.Select(ProviderRole.Vm, options.Vm, Array.Empty<IVmProvider>()),
                ProviderSelector.Select(ProviderRole.Cache, options.Cache, Array.Empty<ICacheProvider>()),
                ProviderSelector.Select(ProviderRole.Search, options.Search, Array.Empty<ISearchProvider>()),
            ]);
        });
        return services;
    }

    /// <summary>Wire codec (JSON, source-generated, behind <see cref="IWireCodec"/>).</summary>
    public static IServiceCollection AddBazaarWire(this IServiceCollection services)
    {
        services.TryAddSingleton<IWireCodec, JsonWireCodec>();
        return services;
    }

    /// <summary>Taint policy, loop guard and usage accounting. In-memory stores stand in until the database repositories (GW3, GW8) are wired.</summary>
    public static IServiceCollection AddBazaarAgentSafety(this IServiceCollection services)
    {
        services.TryAddSingleton(TaintPolicy.Default);
        services.TryAddSingleton(TimeProvider.System);
        services.TryAddSingleton<ILoopGuard>(sp => new LoopGuardFacade(sp.GetRequiredService<TimeProvider>()));
        services.TryAddSingleton<IUsageStore, InMemoryUsageStore>();
        services.TryAddSingleton<UsageRecorder>();
        return services;
    }
}
