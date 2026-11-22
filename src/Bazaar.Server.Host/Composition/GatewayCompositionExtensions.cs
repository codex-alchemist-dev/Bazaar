using Bazaar.Server.Gateway;
using Bazaar.Server.Gateway.Adapters;
using Bazaar.Server.Gateway.Adapters.Anthropic;
using Bazaar.Server.Gateway.Adapters.Gemini;
using Bazaar.Server.Gateway.Adapters.OpenAi;
using Bazaar.Server.Gateway.AnthropicDirect;
using Bazaar.Server.Gateway.ClaudeLane;
using Bazaar.Server.Gateway.Data;
using Bazaar.Server.Gateway.FreeLlmApi;
using Bazaar.Server.Gateway.Health;
using Bazaar.Server.Gateway.Pipeline;
using Bazaar.Server.Gateway.Policy;
using Bazaar.Server.Gateway.Secrets;
using Bazaar.Server.Gateway.Selection;
using Bazaar.Server.Gateway.Usage;
using Bazaar.Server.Host.Components.FreeLlmApi;
using Bazaar.Server.Host.Startup;
using Bazaar.Server.Providers.ClaudeCode;
using Bazaar.Server.Agents.Data;
using Bazaar.Server.Agents.Responder;
using Bazaar.Server.Secrets;
using Microsoft.Extensions.Configuration;
using Bazaar.Server.Gateway.Catalogue;
using Bazaar.Server.Settings;
using Bazaar.Server.Setup.FirstRun.Gateway;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Npgsql;

namespace Bazaar.Server.Host.Composition;

/// <summary>
/// Wires the model gateway (docs/design/05-model-gateway.md) onto the host's database. Database repositories replace the
/// in-memory usage stand-in from <c>AddBazaarAgentSafety</c>. FreeLLMAPI is registered from <c>Bazaar:Gateway:FreeLlmApi</c>
/// and the owner's Anthropic key from <c>Bazaar:Gateway:AnthropicDirect</c>, each only when enabled; otherwise provider secrets
/// fail closed.
/// </summary>
public static class GatewayCompositionExtensions
{
    /// <summary>Adapter name for OpenAI-compatible chat providers, as stored on provider rows. Owned by the adapter component.</summary>
    public const string OpenAiChatAdapterName = OpenAiChatAdapterComponent.WireName;

    /// <summary>Adapter name for Anthropic Messages providers, as stored on provider rows. Owned by the adapter component.</summary>
    public const string AnthropicMessagesAdapterName = AnthropicMessagesAdapterComponent.WireName;

    /// <summary>Adapter name for Gemini generateContent providers, as stored on provider rows. Owned by the adapter component.</summary>
    public const string GeminiGenerateContentAdapterName = GeminiGenerateContentAdapterComponent.WireName;

    /// <summary>
    /// Registers the gateway pipeline and its database-backed stores. Needs <c>AddBazaarHostOptions</c>, <c>AddBazaarSecrets</c>
    /// and <c>AddBazaarHostRuntime</c> to have run first, because the shared <see cref="NpgsqlDataSource"/> comes from
    /// <see cref="DatabaseConnection"/>.
    /// </summary>
    public static IServiceCollection AddBazaarGateway(this IServiceCollection services)
    {
        // Shared pool. DatabaseConnection resolves the secret once and builds the data source without connecting.
        services.TryAddSingleton<NpgsqlDataSource>(sp =>
            sp.GetRequiredService<DatabaseConnection>().GetAsync(CancellationToken.None).AsTask().GetAwaiter().GetResult());

        // The FreeLLMAPI options, validation, key check, diagnostics and HTTP client are registered by FreeLlmApiComponent.
        // TODO(component): the sidecar's hosted service must start before the FreeLLMAPI component's startup key check, so a managed sidecar has written the unified key first.
        services.AddBazaarFreeLlmApiSidecar();
        services.TryAddSingleton<AgentModelPolicyRepository>();
        services.TryAddSingleton<Bazaar.Server.Skimmer.IGatekeeperPolicySeed, GatekeeperPolicySeedAdapter>();

        // The Claude Code terms acceptance lives in settings when a settings store exists; the component's in-memory store is the fallback.
        services.TryAddSingleton<IClaudeCodeAcknowledgementStore>(sp =>
            sp.GetService<Bazaar.Server.Settings.ISettingStore>() is { } store
                ? new SettingsClaudeCodeAcknowledgementStore(store)
                : new InMemoryClaudeCodeAcknowledgementStore());
        services.AddHostedService<GatewayProviderRowSync>();
        services.AddHostedService<DevAgentPolicySeed>();
        services.TryAddSingleton<IClaudeSourceDirectory, GatewayClaudeSourceDirectory>();
        services.TryAddSingleton<IDefaultAgentPolicyStore, GatewayDefaultAgentPolicyStore>();
        services.AddFirstRunGatewayRoutes();
        services.AddSingleton<IAgentDefaultPolicyGrant, AgentDefaultPolicyGrantAdapter>();
        services.AddSingleton<IAgentPolicyPresence, AgentPolicyPresenceAdapter>();
        services.AddBazaarAgentGrantRepair();

        services.TryAddSingleton(new BreakerOptions());
        services.TryAddSingleton<HttpClient>(_ => new HttpClient());
        services.TryAddSingleton<ICatalogueSchedule>(sp =>
            sp.GetService<ISettingStore>() is { } store ? new SettingCatalogueSchedule(store) : new NoCatalogueSchedule());
        services.TryAddSingleton<HealthTracker>(sp =>
            new HealthTracker(sp.GetRequiredService<BreakerOptions>(), sp.GetRequiredService<TimeProvider>()));
        services.TryAddSingleton<UsageRecorder>();
        services.TryAddSingleton<QuotaStateRepository>();
        services.TryAddSingleton<RouteResolver>();
        services.TryAddSingleton<GatewayDirectoryRepository>();
        services.TryAddSingleton<IEmbeddingProfile, PostgresEmbeddingProfile>();

        services.TryAddSingleton<SecretScrubber>();
        services.TryAddSingleton<IProviderSecretSource>(sp => CreateProviderSecretSource(sp));
        services.Replace(ServiceDescriptor.Singleton<IUsageStore, UsageRepository>());
        services.Replace(ServiceDescriptor.Singleton<IAgentPolicyStore, AgentModelPolicyRepository>());
        // Config-defined sources overlay the stored directory: FreeLLMAPI first, then the owner's Anthropic key on top.
        // The Claude Code CLI provider (doc 91) goes on top, so its default route can fall back to the owner's key inside the Claude family.
        services.AddOptions<ClaudeLaneOptions>().BindConfiguration(ClaudeLaneOptions.SectionName).ValidateOnStart();
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IValidateOptions<ClaudeLaneOptions>, ClaudeLaneOptionsValidator>());
        services.Replace(ServiceDescriptor.Singleton<IGatewayDirectory>(sp =>
        {
            var anthropic = AnthropicDirectOptionsOf(sp);
            IGatewayDirectory directory = AnthropicDirectWiring.CreateDirectory(
                FreeLlmApiWiring.CreateDirectory(
                    sp.GetRequiredService<GatewayDirectoryRepository>(),
                    FreeLlmApiOptionsOf(sp),
                    sp.GetRequiredService<TimeProvider>()),
                anthropic,
                sp.GetRequiredService<TimeProvider>());
            var lane = sp.GetRequiredService<IOptions<ClaudeLaneOptions>>().Value;
            IGatewayDirectory routed = lane.Enabled
                ? new ClaudeLaneGatewayDirectory(directory, lane, anthropic, FreeLlmApiOptionsOf(sp), sp.GetRequiredService<TimeProvider>())
                : directory;
            return sp.GetService<IOptions<ClaudeCodeOptions>>()?.Value is { Enabled: true } claudeCode
                ? new ClaudeCodeGatewayDirectory(routed, claudeCode, anthropic, sp.GetRequiredService<TimeProvider>())
                : routed;
        }));
        services.Replace(ServiceDescriptor.Singleton<IAdapterRegistry>(sp => CreateAdapterRegistry(sp)));
        // Endpoint selection (docs/design/92). Nothing changes until a route is bound to a profile, either through
        // IAgentSelectionSetup (the first-run wizard) or Bazaar:Gateway:Selection:Routes:<route> = <profile>.
        services.TryAddSingleton<EndpointStatsTracker>(sp => new EndpointStatsTracker(time: sp.GetRequiredService<TimeProvider>()));
        services.TryAddSingleton<IEndpointStatsTracker>(sp => sp.GetRequiredService<EndpointStatsTracker>());
        services.TryAddSingleton<IPersistentStats>(sp => sp.GetRequiredService<EndpointStatsTracker>());
        services.TryAddSingleton<SelectionInsights>(sp => new SelectionInsights(sp.GetRequiredService<EndpointStatsTracker>(), sp.GetRequiredService<TimeProvider>()));
        services.TryAddSingleton<ISelectionInsights>(sp => sp.GetRequiredService<SelectionInsights>());
        services.AddHostedService(sp => new SelectionStatsPersistence(
            sp.GetRequiredService<IPersistentStats>(),
            sp.GetService<IConfiguration>()?["Bazaar:Gateway:Selection:StatsPath"],
            TimeSpan.FromSeconds(60)));
        services.TryAddSingleton(sp => new EndpointSelector(sp.GetRequiredService<IEndpointStatsTracker>(), time: sp.GetRequiredService<TimeProvider>()));
        services.TryAddSingleton<ProfileBoundRanker>(sp =>
        {
            var ranker = new ProfileBoundRanker(sp.GetRequiredService<EndpointSelector>(), sp.GetRequiredService<SelectionInsights>());
            foreach (var binding in sp.GetService<IConfiguration>()?.GetSection("Bazaar:Gateway:Selection:Routes").GetChildren() ?? [])
            {
                if (!string.IsNullOrWhiteSpace(binding.Value))
                {
                    ranker.BindAsync(binding.Key, binding.Value).GetAwaiter().GetResult();
                }
            }

            // The Claude lane route is always selection-ordered, with identity as a hard tier (doc 05 Claude lane, doc 92).
            var lane = sp.GetRequiredService<IOptions<ClaudeLaneOptions>>().Value;
            if (lane.Enabled)
            {
                ranker.BindAsync(lane.RouteName, new SelectionBinding(lane.Profile, IdentityTiers: true, NativeFirst: lane.NativeFirst)).GetAwaiter().GetResult();
            }

            return ranker;
        });
        services.TryAddSingleton<ICandidateRanker>(sp => sp.GetRequiredService<ProfileBoundRanker>());
        services.TryAddSingleton<IAgentSelectionSetup>(sp => sp.GetRequiredService<ProfileBoundRanker>());
        services.TryAddSingleton<Bazaar.Server.Setup.FirstRun.IDefaultAgentRoutes, ClaudeLaneDefaultAgentRoutes>();
        services.Replace(ServiceDescriptor.Singleton<IModelGateway>(sp => new ModelGateway(
            sp.GetRequiredService<RouteResolver>(),
            sp.GetRequiredService<IGatewayDirectory>(),
            sp.GetRequiredService<IAdapterRegistry>(),
            sp.GetRequiredService<IProviderSecretSource>(),
            sp.GetRequiredService<HealthTracker>(),
            sp.GetRequiredService<UsageRecorder>(),
            prices: LoadPriceTable(sp),
            quota: new RetryAfterQuotaTracker(sp.GetRequiredService<TimeProvider>()),
            time: sp.GetRequiredService<TimeProvider>(),
            onInternalError: ex => GatewayProviderRowSync.LogInternalError(sp.GetRequiredService<ILoggerFactory>().CreateLogger<ModelGateway>(), ex),
            scrubber: sp.GetRequiredService<SecretScrubber>(),
            ranker: sp.GetRequiredService<ICandidateRanker>(),
            selectionStats: sp.GetRequiredService<IEndpointStatsTracker>(),
            events: ComposeQuotaEvents(sp),

            embeddingProfile: sp.GetRequiredService<IEmbeddingProfile>(),
            embeddingRoute: sp.GetService<ISettingStore>() is { } store ? new SettingEmbeddingRouteSource(store) : null)));
        return services;
    }

    /// <summary>
    /// The gateway's quota event sink: any registered <see cref="IGatewayEventSink"/> plus, when the presence sources module is loaded,
    /// its quota source (docs/design/23). Null when neither exists, so the gateway keeps its default.
    /// </summary>
    private static IGatewayEventSink? ComposeQuotaEvents(IServiceProvider sp) =>
        CompositeGatewayEventSink.Compose(
            [
                sp.GetService<IGatewayEventSink>(),
                sp.GetService<Bazaar.Server.Presence.Sources.QuotaPresenceSource>() is { } quota ? new QuotaPresenceGatewayEventSink(quota) : null,
            ],
            ex =>
            {
                if (sp.GetService<ILoggerFactory>() is { } loggers)
                {
                    var log = loggers.CreateLogger<ModelGateway>();
                    GatewayEventSinkLog.SinkFailed(log, ex);
                }
            });

    /// <summary>
    /// The dated price table from the file named by <c>Bazaar:Gateway:PriceTable:Path</c>, or null when none is set. Used for
    /// usage cost and as a ranking hint for endpoint selection (docs/design/92). A set but unreadable file fails the start.
    /// </summary>
    private static PriceTable? LoadPriceTable(IServiceProvider sp)
    {
        var path = sp.GetService<IConfiguration>()?["Bazaar:Gateway:PriceTable:Path"];
        return string.IsNullOrWhiteSpace(path) ? null : PriceTableLoader.Parse(File.ReadAllText(path));
    }

    /// <summary>
    /// The FreeLLMAPI options when <see cref="FreeLlmApiComponent"/> is registered, otherwise the disabled defaults. A disabled
    /// component therefore reads as a disabled FreeLLMAPI: no provider, no route, no key.
    /// </summary>
    private static FreeLlmApiOptions FreeLlmApiOptionsOf(IServiceProvider sp) =>
        sp.GetService<IOptions<FreeLlmApiOptions>>()?.Value ?? new FreeLlmApiOptions();

    /// <summary>
    /// The Anthropic options when <see cref="AnthropicDirectComponent"/> is registered and on, otherwise the disabled defaults:
    /// no provider, no route, no key.
    /// </summary>
    private static AnthropicDirectOptions AnthropicDirectOptionsOf(IServiceProvider sp) =>
        sp.GetService<IOptions<AnthropicDirectOptions>>()?.Value ?? new AnthropicDirectOptions();

    private static IProviderSecretSource CreateProviderSecretSource(IServiceProvider sp) =>
        AnthropicDirectWiring.CreateProviderSecretSource(
            AnthropicDirectOptionsOf(sp),
            sp.GetRequiredService<ISecretResolver>(),
            FreeLlmApiWiring.CreateProviderSecretSource(
                FreeLlmApiOptionsOf(sp),
                sp.GetRequiredService<ISecretResolver>(),
                new GatewaySecretSource(sp.GetRequiredService<ISecretResolver>())));

    /// <summary>Builds the registry from every adapter the enabled components registered. Two registrations for one name fail the start.</summary>
    private static DictionaryAdapterRegistry CreateAdapterRegistry(IServiceProvider sp)
    {
        Dictionary<string, IModelAdapter> adapters = new(StringComparer.Ordinal);
        foreach (var registration in sp.GetServices<ModelAdapterRegistration>())
        {
            if (!adapters.TryAdd(registration.WireName, registration.Create(sp)))
            {
                throw new InvalidOperationException($"More than one component registers the gateway adapter '{registration.WireName}'.");
            }
        }

        return new DictionaryAdapterRegistry(adapters);
    }
}
