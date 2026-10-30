using Bazaar.Server.Gateway;
using Bazaar.Server.Gateway.Adapters;
using Bazaar.Server.Gateway.Adapters.Anthropic;
using Bazaar.Server.Gateway.Adapters.Gemini;
using Bazaar.Server.Gateway.Adapters.OpenAi;
using Bazaar.Server.Gateway.Data;
using Bazaar.Server.Gateway.FreeLlmApi;
using Bazaar.Server.Gateway.Health;
using Bazaar.Server.Gateway.Pipeline;
using Bazaar.Server.Gateway.Policy;
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
/// when enabled; otherwise provider secrets fail closed.
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

        // Bound from the host configuration when one is registered. Without it the options keep their disabled defaults.
        services.AddOptions<FreeLlmApiOptions>()
            .Configure<IServiceProvider>((options, sp) =>
            {
                sp.GetService<IConfiguration>()?.GetSection(FreeLlmApiOptions.SectionName).Bind(options);
                options.ApplyManagedSidecarDefaults();
            })
            .ValidateOnStart();
        services.AddSingleton<IValidateOptions<FreeLlmApiOptions>, FreeLlmApiOptionsValidator>();
        // TODO(component): registered before the startup key check, so a managed sidecar has written the unified key before the check reads it.
        services.AddBazaarFreeLlmApiSidecar();
        services.AddHostedService<FreeLlmApiStartupCheckService>();

        services.TryAddSingleton(new BreakerOptions());
        services.TryAddSingleton<HttpClient>(_ => new HttpClient());
        services.TryAddSingleton<HealthTracker>(sp =>
            new HealthTracker(sp.GetRequiredService<BreakerOptions>(), sp.GetRequiredService<TimeProvider>()));
        services.TryAddSingleton<UsageRecorder>();
        services.TryAddSingleton<QuotaStateRepository>();
        services.TryAddSingleton<RouteResolver>();
        services.TryAddSingleton<FreeLlmApiRoutingDiagnostics>();
        services.TryAddSingleton<GatewayDirectoryRepository>();

        services.TryAddSingleton<IProviderSecretSource>(sp => CreateProviderSecretSource(sp));
        services.Replace(ServiceDescriptor.Singleton<IUsageStore, UsageRepository>());
        services.Replace(ServiceDescriptor.Singleton<IAgentPolicyStore, AgentModelPolicyRepository>());
        services.Replace(ServiceDescriptor.Singleton<IGatewayDirectory>(sp => FreeLlmApiWiring.CreateDirectory(
            sp.GetRequiredService<GatewayDirectoryRepository>(),
            sp.GetRequiredService<IOptions<FreeLlmApiOptions>>().Value,
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
            time: sp.GetRequiredService<TimeProvider>())));
        return services;
    }

    private static IProviderSecretSource CreateProviderSecretSource(IServiceProvider sp) =>
        FreeLlmApiWiring.CreateProviderSecretSource(
            sp.GetRequiredService<IOptions<FreeLlmApiOptions>>().Value,
            sp.GetRequiredService<ISecretResolver>(),
            new UnconfiguredProviderSecretSource());

    private static DictionaryAdapterRegistry CreateAdapterRegistry(IServiceProvider sp)
    {
        var http = sp.GetRequiredService<HttpClient>();
        var time = sp.GetRequiredService<TimeProvider>();
        var freeLlmApi = sp.GetRequiredService<IOptions<FreeLlmApiOptions>>().Value;
        var freeLlmApiHttp = freeLlmApi.Enabled ? FreeLlmApiWiring.CreateHttpClient(freeLlmApi) : http;
        IModelAdapter openAi = FreeLlmApiWiring.WrapOpenAiAdapter(
            new OpenAiChatAdapter(http, time),
            freeLlmApi,
            freeLlmApiHttp,
            time,
            sp.GetRequiredService<FreeLlmApiRoutingDiagnostics>());
        Dictionary<string, IModelAdapter> adapters = new(StringComparer.Ordinal)
        {
            [OpenAiChatAdapterName] = openAi,
            [AnthropicMessagesAdapterName] = new AnthropicMessagesAdapter(http),
            [GeminiGenerateContentAdapterName] = new GeminiGenerateContentAdapter(http),
        };
        return new DictionaryAdapterRegistry(adapters);
    }
}

/// <summary>
/// Fail-closed <see cref="IProviderSecretSource"/> used for every provider reference that is not the FreeLLMAPI key. Every call
/// throws, and the gateway turns that into a skipped candidate, so no other provider is ever called without a key.
/// </summary>
public sealed class UnconfiguredProviderSecretSource : IProviderSecretSource
{
    /// <summary>Always throws. The reference is not echoed, so it cannot reach a log or an error response.</summary>
    public ValueTask<string?> ResolveAsync(string secretRef, CancellationToken ct) =>
        throw new InvalidOperationException("Provider secrets are not configured yet; they arrive with the Identity Service.");
}

/// <summary>
/// Checks at start that the configured FreeLLMAPI key resolves. A disabled config returns at once and reads no secret. A
/// missing key stops the start with the configuration key named in the error.
/// </summary>
public sealed class FreeLlmApiStartupCheckService(IOptions<FreeLlmApiOptions> options, ISecretResolver resolver) : IHostedService
{
    public Task StartAsync(CancellationToken cancellationToken) =>
        FreeLlmApiStartupCheck.VerifyKeyAsync(options.Value, resolver, cancellationToken);

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
