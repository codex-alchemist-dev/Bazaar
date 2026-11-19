using Bazaar.Server.Gateway;
using Bazaar.Server.Gateway.Adapters;
using Bazaar.Server.Gateway.Adapters.Anthropic;
using Bazaar.Server.Gateway.Adapters.Gemini;
using Bazaar.Server.Gateway.Adapters.OpenAi;
using Bazaar.Server.Gateway.AnthropicDirect;
using Bazaar.Server.Gateway.Data;
using Bazaar.Server.Gateway.FreeLlmApi;
using Bazaar.Server.Gateway.Health;
using Bazaar.Server.Gateway.Pipeline;
using Bazaar.Server.Gateway.Policy;
using Bazaar.Server.Gateway.Secrets;
using Bazaar.Server.Gateway.Usage;
using Bazaar.Server.Host.Components.FreeLlmApi;
using Bazaar.Server.Host.Startup;
using Bazaar.Server.Secrets;
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
        services.AddHostedService<DevAgentPolicySeed>();

        services.TryAddSingleton(new BreakerOptions());
        services.TryAddSingleton<HttpClient>(_ => new HttpClient());
        services.TryAddSingleton<HealthTracker>(sp =>
            new HealthTracker(sp.GetRequiredService<BreakerOptions>(), sp.GetRequiredService<TimeProvider>()));
        services.TryAddSingleton<UsageRecorder>();
        services.TryAddSingleton<QuotaStateRepository>();
        services.TryAddSingleton<RouteResolver>();
        services.TryAddSingleton<GatewayDirectoryRepository>();

        services.TryAddSingleton<SecretScrubber>();
        services.TryAddSingleton<IProviderSecretSource>(sp => CreateProviderSecretSource(sp));
        services.Replace(ServiceDescriptor.Singleton<IUsageStore, UsageRepository>());
        services.Replace(ServiceDescriptor.Singleton<IAgentPolicyStore, AgentModelPolicyRepository>());
        // Config-defined sources overlay the stored directory: FreeLLMAPI first, then the owner's Anthropic key on top.
        services.Replace(ServiceDescriptor.Singleton<IGatewayDirectory>(sp => AnthropicDirectWiring.CreateDirectory(
            FreeLlmApiWiring.CreateDirectory(
                sp.GetRequiredService<GatewayDirectoryRepository>(),
                FreeLlmApiOptionsOf(sp),
                sp.GetRequiredService<TimeProvider>()),
            AnthropicDirectOptionsOf(sp),
            sp.GetRequiredService<TimeProvider>())));
        services.Replace(ServiceDescriptor.Singleton<IAdapterRegistry>(sp => CreateAdapterRegistry(sp)));
        services.Replace(ServiceDescriptor.Singleton<IModelGateway>(sp => new ModelGateway(
            sp.GetRequiredService<RouteResolver>(),
            sp.GetRequiredService<IGatewayDirectory>(),
            sp.GetRequiredService<IAdapterRegistry>(),
            sp.GetRequiredService<IProviderSecretSource>(),
            sp.GetRequiredService<HealthTracker>(),
            sp.GetRequiredService<UsageRecorder>(),
            quota: new RetryAfterQuotaTracker(sp.GetRequiredService<TimeProvider>()),
            time: sp.GetRequiredService<TimeProvider>(),
            scrubber: sp.GetRequiredService<SecretScrubber>())));
        return services;
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

