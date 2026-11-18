using Bazaar.Components.ManagedPostgres;
using Bazaar.Components.ManagedPostgres.Secrets;
using Bazaar.Server.Agents.LoopGuard;
using Bazaar.Server.Agents.Recovery;
using Bazaar.Server.Gateway.Usage;
using Bazaar.Server.Host.Recovery;
using Bazaar.Server.Host.Startup;
using Bazaar.Server.Host.Status;
using Bazaar.Server.Hosting;
using Bazaar.Server.Providers;
using Bazaar.Server.Providers.Linux;
using Bazaar.Server.Providers.Windows;
using Bazaar.Server.Providers.Windows.Postgres;
using Bazaar.Server.Secrets;
using Bazaar.Server.Security.Taint;
using Bazaar.Server.Wire.Connection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;

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
        // Managed PostgreSQL (packaging.md P5). External mode is the default and leaves the configured secret reference in charge.
        // Windows keeps the managed superuser password under DPAPI. The component's file store refuses to run there.
        if (OperatingSystem.IsWindows())
        {
            var passwordPath = (configuration.GetSection(ManagedPostgresOptions.SectionName).Get<ManagedPostgresOptions>() ?? new ManagedPostgresOptions())
                .ResolvedPasswordStorePath;
            services.TryAddSingleton<IManagedPasswordStore>(new DpapiManagedPasswordStore(passwordPath));
        }

        services.AddBazaarManagedPostgres(configuration);
        services.TryAddSingleton<DatabaseConnection>();
        // Module repositories take the shared data source; resolving it blocks once on the bootstrap secret read.
        services.TryAddSingleton(sp => sp.GetRequiredService<DatabaseConnection>().GetAsync(CancellationToken.None).AsTask().GetAwaiter().GetResult());
        services.TryAddSingleton<IInstanceLock, PostgresInstanceLock>();
        services.TryAddSingleton<ISchemaGate, MigrationSchemaGate>();
        services.AddSingleton<IReadinessCheck, DatabaseReadinessCheck>();
        services.AddSingleton<IReadinessCheck, InstanceLockReadinessCheck>();
        services.AddSingleton<IReadinessCheck, ProviderReadinessCheck>();
        services.AddHostedService<StartupSequence>();
        return services;
    }

    /// <summary>
    /// Doc 03 WP5: startup lease recovery sweep (step 6) and shutdown checkpoint. Call after <see cref="AddBazaarHostRuntime"/> so the hosted
    /// service starts after <c>StartupSequence</c>. TODO(component): move into the component model once PR #125 merges.
    /// </summary>
    public static IServiceCollection AddBazaarRecoveryHook(this IServiceCollection services, IConfiguration configuration)
    {
        services.TryAddSingleton(TimeProvider.System);
        services.AddOptions<RecoveryHookOptions>().Bind(configuration.GetSection(RecoveryHookOptions.SectionName));
        services.TryAddSingleton<AgentLeaseRecovery>();
        services.TryAddSingleton<ILeaseRecoveryRunner, LeaseRecoveryRunner>();
        services.TryAddSingleton<RecoveryHookState>();
        services.AddSingleton<IReadinessCheck, RecoverySweepReadinessCheck>();
        services.AddHostedService<RecoveryHostedService>();
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
    /// Picks one provider per role at start (doc 03). The service host and the cache have OS implementations so far; the other roles default to
    /// <c>none</c> until their providers land. Explicit choices that are not usable fail the start with no silent fallback.
    /// </summary>
    public static IServiceCollection AddBazaarProviders(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddOptions<BazaarProviderOptions>().Bind(configuration.GetSection(BazaarProviderOptions.SectionName));
        // Consumers of ICacheProvider get the deferred wrapper: misses and dropped writes until CacheSelectionStep resolves the cache.
        services.TryAddSingleton<DeferredCacheProvider>();
        services.AddSingleton<ICacheProvider>(sp => sp.GetRequiredService<DeferredCacheProvider>());
        services.TryAddSingleton(sp =>
        {
            var options = sp.GetRequiredService<IOptions<BazaarProviderOptions>>().Value;
            IReadOnlyList<IServiceHostProvider> serviceHosts = [.. WindowsProviders.ServiceHostCandidates(), .. LinuxProviders.ServiceHostCandidates()];
            // The cache is not probed here. CacheSelectionStep selects it after the instance lock and the managed child (doc 40 section 4b.4 item 8).
            // Host options (and their validation) are read only when the VM role is enabled, so a host that disables it needs no data root.
            IReadOnlyList<IVmProvider> vms = options.Vm.Equals(ProviderSelector.None, StringComparison.OrdinalIgnoreCase)
                ? []
                : WindowsProviders.VmCandidates(sp.GetRequiredService<IOptions<BazaarHostOptions>>().Value.DataRoot);
            return new ProviderCapabilityReport(
            [
                ProviderSelector.Select(ProviderRole.ServiceHost, options.ServiceHost, serviceHosts),
                ProviderSelector.Select(ProviderRole.Vm, options.Vm, vms, ProviderRequirements.AgentPairConcurrency),
                CacheSelection(options.Cache),
                ProviderSelector.Select(ProviderRole.Search, options.Search, Array.Empty<ISearchProvider>()),
            ]);
        });
        return services;
    }

    /// <summary>
    /// The cache role at startup. <c>none</c> is decided at once. Any other request is pending until <see cref="CacheSelectionStep"/> runs,
    /// so the cache, which is optional and non-critical (C07), never delays or fails the instance lock or the other roles.
    /// </summary>
    private static ProviderSelection CacheSelection(string requested) =>
        requested.Equals(ProviderSelector.None, StringComparison.OrdinalIgnoreCase)
            ? ProviderSelector.Select(ProviderRole.Cache, requested, Array.Empty<ICacheProvider>())
            : ProviderSelection.Pending(ProviderRole.Cache, requested);

    /// <summary>
    /// Registers the deferred cache selection. Call after <see cref="AddBazaarComponents"/>: hosted services start in registration order, and
    /// the Valkey supervisor is registered by a component. The step then runs after the instance lock (<c>StartupSequence</c>) and after the
    /// supervisor has started the managed child.
    /// </summary>
    public static IServiceCollection AddBazaarCacheSelection(this IServiceCollection services)
    {
        services.AddHostedService<CacheSelectionStep>();
        return services;
    }

    /// <summary>
    /// Wire connection manager and codec, with a fail-closed authenticator until the Identity Service supplies the real one.
    /// The socket endpoint (<c>UseBazaarWire</c>) is not mapped yet: it waits for authentication.
    /// </summary>
    public static IServiceCollection AddBazaarHostWire(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddBazaarWire(configuration);
        services.TryAddSingleton<IConnectionAuthenticator, DenyAllAuthenticator>();
        return services;
    }

    /// <summary>Taint policy, loop guard and usage accounting. In-memory stores stand in until the database repositories (GW3, GW8) are wired.</summary>
    public static IServiceCollection AddBazaarAgentSafety(this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        services.TryAddSingleton(TaintPolicy.Default);
        services.TryAddSingleton(TimeProvider.System);
        services.AddOptions<LoopGuardOptions>()
            .BindConfiguration(LoopGuardOptions.SectionName)
            .Validate(options =>
            {
                try
                {
                    options.Validate();
                    return true;
                }
                catch (ArgumentException)
                {
                    return false;
                }
            }, "Loop guard limits are invalid.");
        services.TryAddSingleton<ILoopGuard>(sp => new LoopGuardFacade(
            sp.GetRequiredService<TimeProvider>(),
            sp.GetRequiredService<IOptions<LoopGuardOptions>>().Value));
        services.TryAddSingleton<IUsageStore, InMemoryUsageStore>();
        services.TryAddSingleton<UsageRecorder>();
        return services;
    }
}
