using Bazaar.Server.Gateway;
using Bazaar.Server.Gateway.Adapters.Anthropic;
using Bazaar.Server.Gateway.Adapters.Gemini;
using Bazaar.Server.Gateway.Adapters.OpenAi;
using Bazaar.Server.Gateway.Data;
using Bazaar.Server.Gateway.Pipeline;
using Bazaar.Server.Gateway.Policy;
using Bazaar.Server.Gateway.Usage;
using Bazaar.Server.Host.Composition;
using Bazaar.Server.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Bazaar.Server.Host.Tests;

public sealed class GatewayCompositionTests
{
    private const string _envName = "BAZAAR_GATEWAY_COMPOSITION_TEST_DB";
    private const string _connectionString = "Host=localhost;Database=x";

    private static ServiceProvider Build(Action<IServiceCollection>? beforeGateway = null)
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            [$"{BazaarHostOptions.SectionName}:DataRoot"] = Path.Combine(Path.GetTempPath(), "bazaar-gateway-tests"),
            [$"{BazaarHostOptions.SectionName}:DatabaseConnectionReference"] = "env:" + _envName,
        }).Build();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddBazaarHostOptions(configuration);
        services.AddBazaarHostRuntime(configuration);
        services.AddBazaarSecrets();
        services.AddBazaarAgentSafety();
        beforeGateway?.Invoke(services);
        services.AddBazaarGateway();
        return services.BuildServiceProvider(validateScopes: true);
    }

    [Fact]
    public async Task Gateway_services_resolve_to_the_database_backed_types()
    {
        Environment.SetEnvironmentVariable(_envName, _connectionString);
        try
        {
            await using var sp = Build();
            Assert.IsType<ModelGateway>(sp.GetRequiredService<IModelGateway>());
            Assert.IsType<UsageRepository>(sp.GetRequiredService<IUsageStore>());
            Assert.IsType<AgentModelPolicyRepository>(sp.GetRequiredService<IAgentPolicyStore>());
            Assert.IsType<GatewayDirectoryRepository>(sp.GetRequiredService<IGatewayDirectory>());
            Assert.IsType<QuotaStateRepository>(sp.GetRequiredService<QuotaStateRepository>());
            Assert.IsType<RouteResolver>(sp.GetRequiredService<RouteResolver>());
            Assert.IsType<DictionaryAdapterRegistry>(sp.GetRequiredService<IAdapterRegistry>());
        }
        finally
        {
            Environment.SetEnvironmentVariable(_envName, null);
        }
    }

    [Fact]
    public async Task Adapter_registry_maps_each_adapter_name_to_its_adapter()
    {
        Environment.SetEnvironmentVariable(_envName, _connectionString);
        try
        {
            await using var sp = Build();
            var registry = sp.GetRequiredService<IAdapterRegistry>();
            Assert.IsType<OpenAiChatAdapter>(registry.Get(GatewayCompositionExtensions.OpenAiChatAdapterName));
            Assert.IsType<AnthropicMessagesAdapter>(registry.Get(GatewayCompositionExtensions.AnthropicMessagesAdapterName));
            Assert.IsType<GeminiGenerateContentAdapter>(registry.Get(GatewayCompositionExtensions.GeminiGenerateContentAdapterName));
            Assert.Null(registry.Get("no_such_adapter"));
        }
        finally
        {
            Environment.SetEnvironmentVariable(_envName, null);
        }
    }

    [Fact]
    public async Task Provider_secrets_fail_closed_until_the_identity_service_lands()
    {
        Environment.SetEnvironmentVariable(_envName, _connectionString);
        try
        {
            await using var sp = Build();
            var source = sp.GetRequiredService<IProviderSecretSource>();
            Assert.IsType<UnconfiguredProviderSecretSource>(source);
            var ex = await Assert.ThrowsAsync<InvalidOperationException>(async () =>
                await source.ResolveAsync("env:SOME_PROVIDER_KEY", TestContext.Current.CancellationToken));
            Assert.Contains("Identity Service", ex.Message, StringComparison.Ordinal);
            Assert.DoesNotContain("SOME_PROVIDER_KEY", ex.Message, StringComparison.Ordinal);
        }
        finally
        {
            Environment.SetEnvironmentVariable(_envName, null);
        }
    }

    [Fact]
    public async Task A_real_secret_source_registered_earlier_wins_over_the_placeholder()
    {
        Environment.SetEnvironmentVariable(_envName, _connectionString);
        try
        {
            await using var sp = Build(services => services.AddSingleton<IProviderSecretSource, FixedSecretSource>());
            Assert.IsType<FixedSecretSource>(sp.GetRequiredService<IProviderSecretSource>());
        }
        finally
        {
            Environment.SetEnvironmentVariable(_envName, null);
        }
    }

    private sealed class FixedSecretSource : IProviderSecretSource
    {
        public ValueTask<string?> ResolveAsync(string secretRef, CancellationToken ct) => ValueTask.FromResult<string?>("test-key");
    }
}
