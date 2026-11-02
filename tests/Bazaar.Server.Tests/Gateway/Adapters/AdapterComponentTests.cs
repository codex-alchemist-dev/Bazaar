using Bazaar.Server.Components;
using Bazaar.Server.Gateway.Adapters;
using Bazaar.Server.Gateway.Adapters.Anthropic;
using Bazaar.Server.Gateway.Adapters.Gemini;
using Bazaar.Server.Gateway.Adapters.OpenAi;
using Bazaar.Server.Gateway.FreeLlmApi;
using Bazaar.Server.Gateway.Pipeline;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Bazaar.Server.Tests.Gateway.Adapters;

public class AdapterComponentTests
{
    private static IBazaarComponent[] AdapterComponents() =>
    [
        new OpenAiChatAdapterComponent(),
        new AnthropicMessagesAdapterComponent(),
        new GeminiGenerateContentAdapterComponent(),
    ];

    [Fact]
    public void Adapter_manifests_are_adapter_components_with_unique_ids_and_provides()
    {
        var manifests = AdapterComponents().Select(c => c.Manifest).ToList();

        Assert.All(manifests, m =>
        {
            Assert.Equal(ComponentKind.Adapter, m.Kind);
            Assert.Equal("0.1.0", m.Version);
            Assert.Empty(m.DependsOn);
        });
        Assert.Equal(
            ["adapter-anthropic-messages", "adapter-gemini-generate-content", "adapter-openai-chat"],
            manifests.Select(m => m.Id).Order(StringComparer.Ordinal));
        Assert.Equal(
            ["gateway:adapter:anthropic_messages", "gateway:adapter:gemini_generate_content", "gateway:adapter:openai_chat"],
            manifests.SelectMany(m => m.Provides).Order(StringComparer.Ordinal));
        Assert.Equal(manifests.Count, manifests.Select(m => m.Id).Distinct(StringComparer.Ordinal).Count());
        Assert.Equal(manifests.Count, manifests.SelectMany(m => m.Provides).Distinct(StringComparer.Ordinal).Count());
    }

    [Fact]
    public void Adapter_components_pass_the_kernel_graph_check()
    {
        var ordered = ComponentGraph.Order(AdapterComponents().Select(c => c.Manifest));

        Assert.Equal(3, ordered.Count);
    }

    [Fact]
    public void Each_adapter_component_contributes_one_registration_under_its_wire_name()
    {
        var services = new ServiceCollection();
        services.AddSingleton(new HttpClient());
        services.AddSingleton(TimeProvider.System);
        services.AddSingleton(Options.Create(new FreeLlmApiOptions()));
        services.AddSingleton(new FreeLlmApiRoutingDiagnostics());

        services.AddBazaarComponents(new ConfigurationBuilder().Build(), AdapterComponents());

        using var sp = services.BuildServiceProvider(validateScopes: true);
        var registrations = sp.GetServices<ModelAdapterRegistration>().ToDictionary(r => r.WireName, StringComparer.Ordinal);
        Assert.Equal(3, registrations.Count);
        Assert.IsType<OpenAiChatAdapter>(registrations["openai_chat"].Create(sp));
        Assert.IsType<AnthropicMessagesAdapter>(registrations["anthropic_messages"].Create(sp));
        Assert.IsType<GeminiGenerateContentAdapter>(registrations["gemini_generate_content"].Create(sp));
    }

    [Fact]
    public void Disabled_adapter_component_contributes_no_registration()
    {
        var services = new ServiceCollection();
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Bazaar:Components:adapter-anthropic-messages:Enabled"] = "false",
        }).Build();

        services.AddBazaarComponents(configuration, AdapterComponents());

        using var sp = services.BuildServiceProvider();
        Assert.DoesNotContain(sp.GetServices<ModelAdapterRegistration>(), r => r.WireName == "anthropic_messages");
    }
}
