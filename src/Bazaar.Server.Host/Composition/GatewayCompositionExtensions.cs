using Bazaar.Server.Gateway;
using Bazaar.Server.Gateway.Adapters;
using Bazaar.Server.Gateway.Adapters.Anthropic;
using Bazaar.Server.Gateway.Adapters.Gemini;
using Bazaar.Server.Gateway.Adapters.OpenAi;
using Bazaar.Server.Gateway.Data;
using Bazaar.Server.Gateway.Health;
using Bazaar.Server.Gateway.Pipeline;
using Bazaar.Server.Gateway.Policy;
using Bazaar.Server.Gateway.Usage;
using Bazaar.Server.Host.Startup;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Npgsql;

namespace Bazaar.Server.Host.Composition;

/// <summary>
/// Wires the model gateway (docs/design/05-model-gateway.md) onto the host's database. Database repositories replace the
/// in-memory usage stand-in from <c>AddBazaarAgentSafety</c>. Provider secrets fail closed until the Identity Service lands.
/// </summary>
public static class GatewayCompositionExtensions
{
    /// <summary>Adapter name for OpenAI-compatible chat providers, as stored on provider rows.</summary>
    public const string OpenAiChatAdapterName = "openai_chat";

    /// <summary>Adapter name for Anthropic Messages providers, as stored on provider rows.</summary>
    public const string AnthropicMessagesAdapterName = "anthropic_messages";

    /// <summary>Adapter name for Gemini generateContent providers, as stored on provider rows.</summary>
    public const string GeminiGenerateContentAdapterName = "gemini_generate_content";

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

        services.TryAddSingleton(new BreakerOptions());
        services.TryAddSingleton<HttpClient>(_ => new HttpClient());
        services.TryAddSingleton<HealthTracker>(sp =>
            new HealthTracker(sp.GetRequiredService<BreakerOptions>(), sp.GetRequiredService<TimeProvider>()));
        services.TryAddSingleton<UsageRecorder>();
        services.TryAddSingleton<QuotaStateRepository>();
        services.TryAddSingleton<RouteResolver>();
        services.TryAddSingleton<IProviderSecretSource, UnconfiguredProviderSecretSource>();

        services.Replace(ServiceDescriptor.Singleton<IUsageStore, UsageRepository>());
        services.Replace(ServiceDescriptor.Singleton<IAgentPolicyStore, AgentModelPolicyRepository>());
        services.Replace(ServiceDescriptor.Singleton<IGatewayDirectory, GatewayDirectoryRepository>());
        services.Replace(ServiceDescriptor.Singleton<IAdapterRegistry>(sp => CreateAdapterRegistry(sp)));
        services.Replace(ServiceDescriptor.Singleton<IModelGateway>(sp => new ModelGateway(
            sp.GetRequiredService<RouteResolver>(),
            sp.GetRequiredService<IGatewayDirectory>(),
            sp.GetRequiredService<IAdapterRegistry>(),
            sp.GetRequiredService<IProviderSecretSource>(),
            sp.GetRequiredService<HealthTracker>(),
            sp.GetRequiredService<UsageRecorder>(),
            time: sp.GetRequiredService<TimeProvider>())));
        return services;
    }

    private static DictionaryAdapterRegistry CreateAdapterRegistry(IServiceProvider sp)
    {
        var http = sp.GetRequiredService<HttpClient>();
        var time = sp.GetRequiredService<TimeProvider>();
        Dictionary<string, IModelAdapter> adapters = new(StringComparer.Ordinal)
        {
            [OpenAiChatAdapterName] = new OpenAiChatAdapter(http, time),
            [AnthropicMessagesAdapterName] = new AnthropicMessagesAdapter(http),
            [GeminiGenerateContentAdapterName] = new GeminiGenerateContentAdapter(http),
        };
        return new DictionaryAdapterRegistry(adapters);
    }
}

/// <summary>
/// Fail-closed <see cref="IProviderSecretSource"/> used until provider secrets arrive with the Identity Service. Every call
/// throws, and the gateway turns that into a skipped candidate, so no provider is ever called without a key.
/// </summary>
public sealed class UnconfiguredProviderSecretSource : IProviderSecretSource
{
    /// <summary>Always throws. The reference is not echoed, so it cannot reach a log or an error response.</summary>
    public ValueTask<string?> ResolveAsync(string secretRef, CancellationToken ct) =>
        throw new InvalidOperationException("Provider secrets are not configured yet; they arrive with the Identity Service.");
}
