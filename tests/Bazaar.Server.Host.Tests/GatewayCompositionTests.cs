using Bazaar.Server.Components;
using Bazaar.Server.Gateway;
using Bazaar.Server.Gateway.Adapters.Anthropic;
using Bazaar.Server.Gateway.Adapters.Gemini;
using Bazaar.Server.Gateway.Adapters.OpenAi;
using Bazaar.Server.Gateway.AnthropicDirect;
using Bazaar.Server.Gateway.Data;
using Bazaar.Server.Gateway.Pipeline;
using Bazaar.Server.Gateway.Policy;
using Bazaar.Server.Gateway.Secrets;
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
        services.AddBazaarAgentSafety(new ConfigurationBuilder().Build());
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
            // The three wire-family adapters arrive through their Bazaar Components, as the host registers them.
            await using var sp = Build(s => s.AddBazaarComponents(new ConfigurationBuilder().Build(), [
                new OpenAiChatAdapterComponent(),
                new AnthropicMessagesAdapterComponent(),
                new GeminiGenerateContentAdapterComponent(),
            ]));
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
    public async Task Provider_secrets_accept_only_provider_references_and_never_fall_back_to_env()
    {
        Environment.SetEnvironmentVariable(_envName, _connectionString);
        try
        {
            await using var sp = Build();
            var source = sp.GetRequiredService<IProviderSecretSource>();
            Assert.IsType<GatewaySecretSource>(source);
            var ex = await Assert.ThrowsAsync<ArgumentException>(async () =>
                await source.ResolveAsync("env:SOME_PROVIDER_KEY", TestContext.Current.CancellationToken));
            Assert.DoesNotContain("SOME_PROVIDER_KEY", ex.Message, StringComparison.Ordinal);
            Assert.Null(await source.ResolveAsync("secret://bazaar/providers/a/key", TestContext.Current.CancellationToken));
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

    [Fact]
    public async Task Anthropic_direct_is_absent_by_default_and_adds_its_provider_and_route_when_enabled()
    {
        Environment.SetEnvironmentVariable(_envName, _connectionString);
        try
        {
            // Off (the default): the stored directory is used as it is.
            await using (var off = Build(s => s.AddBazaarComponents(new ConfigurationBuilder().Build(), [new AnthropicDirectComponent()])))
            {
                Assert.IsType<GatewayDirectoryRepository>(off.GetRequiredService<IGatewayDirectory>());
                Assert.IsType<GatewaySecretSource>(off.GetRequiredService<IProviderSecretSource>());
            }

            var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
            {
                [$"{AnthropicDirectOptions.SectionName}:Enabled"] = "true",
                [$"{AnthropicDirectOptions.SectionName}:ApiKey"] = "env:BAZAAR_TEST_ANTHROPIC_KEY_NOT_SET",
            }).Build();
            await using var on = Build(s => s.AddBazaarComponents(configuration, [new AnthropicDirectComponent()]));
            var directory = on.GetRequiredService<IGatewayDirectory>();
            Assert.IsType<AnthropicDirectGatewayDirectory>(directory);
            var provider = await directory.GetProviderAsync(AnthropicDirectProvider.ProviderId, TestContext.Current.CancellationToken);
            Assert.NotNull(provider);
            Assert.Equal(GatewayCompositionExtensions.AnthropicMessagesAdapterName, provider.Adapter);
            Assert.NotNull(await directory.GetRouteAsync(AnthropicDirectOptions.DefaultModel, TestContext.Current.CancellationToken));
            Assert.IsType<AnthropicDirectSecretSource>(on.GetRequiredService<IProviderSecretSource>());
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
