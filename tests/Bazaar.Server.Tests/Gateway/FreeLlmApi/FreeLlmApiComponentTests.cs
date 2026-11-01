using Bazaar.Server.Components;
using Bazaar.Server.Gateway.FreeLlmApi;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace Bazaar.Server.Tests.Gateway.FreeLlmApi;

/// <summary>
/// The FreeLLMAPI Bazaar Component: its manifest, that a disabled FreeLLMAPI registers nothing that contacts the network, and that
/// an enabled one validates its options the same way the gateway always did. Every key here is a placeholder, never a credential.
/// </summary>
public sealed class FreeLlmApiComponentTests
{
    private const string _keyReference = "env:FREELLMAPI_TEST_KEY";

    private static IConfiguration Config(params (string Key, string Value)[] values) =>
        new ConfigurationBuilder().AddInMemoryCollection(values.ToDictionary(v => v.Key, v => (string?)v.Value)).Build();

    private static (string Key, string Value)[] EnabledValues(string baseUrl = "http://127.0.0.1:3001/v1") =>
    [
        ("Bazaar:Gateway:FreeLlmApi:Enabled", "true"),
        ("Bazaar:Gateway:FreeLlmApi:BaseUrl", baseUrl),
        ("Bazaar:Gateway:FreeLlmApi:ApiKey", _keyReference),
        ("Bazaar:Gateway:FreeLlmApi:DefaultModel", "llama-3.3-70b"),
    ];

    private static ServiceCollection Register(IConfiguration configuration)
    {
        var services = new ServiceCollection();
        services.AddBazaarComponents(configuration, [new FreeLlmApiComponent()]);
        return services;
    }

    [Fact]
    public void Manifest_is_a_provider_with_no_dependencies_that_provides_the_freellmapi_gateway_role()
    {
        var manifest = new FreeLlmApiComponent().Manifest;

        Assert.Equal("freellmapi", manifest.Id);
        Assert.Equal("0.1.0", manifest.Version);
        Assert.Equal(ComponentKind.Provider, manifest.Kind);
        Assert.Empty(manifest.DependsOn);
        Assert.Single(manifest.Provides);
        Assert.Equal("gateway:provider:freellmapi", manifest.Provides[0]);
    }

    [Fact]
    public void Gateway_on_with_the_component_disabled_fails_startup_and_names_both_settings()
    {
        var configuration = Config([.. EnabledValues(), ("Bazaar:Components:freellmapi:Enabled", "false")]);

        var ex = Assert.Throws<InvalidOperationException>(() => FreeLlmApiComponent.RequireComponentWhenGatewayIsOn(configuration));

        Assert.Contains("Bazaar:Gateway:FreeLlmApi:Enabled", ex.Message);
        Assert.Contains("Bazaar:Components:freellmapi:Enabled=false", ex.Message);
    }

    [Fact]
    public void Gateway_on_with_the_component_enabled_passes_the_guard()
    {
        FreeLlmApiComponent.RequireComponentWhenGatewayIsOn(Config(EnabledValues()));
    }

    [Fact]
    public void Gateway_off_with_the_component_disabled_passes_the_guard()
    {
        FreeLlmApiComponent.RequireComponentWhenGatewayIsOn(Config([("Bazaar:Components:freellmapi:Enabled", "false")]));
    }

    [Fact]
    public void With_FreeLlmApi_disabled_the_component_registers_no_startup_check_no_validator_and_no_http_client()
    {
        var services = Register(Config());

        Assert.DoesNotContain(services, d => d.ServiceType == typeof(IHostedService));
        Assert.DoesNotContain(services, d => d.ServiceType == typeof(IValidateOptions<FreeLlmApiOptions>));
        Assert.DoesNotContain(services, d => d.ServiceKey is not null && Equals(d.ServiceKey, FreeLlmApiComponent.HttpClientKey));
        Assert.DoesNotContain(services, d => d.ServiceType == typeof(FreeLlmApiRoutingDiagnostics));
        Assert.Empty(services);
    }

    [Fact]
    public void An_explicit_disabled_flag_registers_nothing_even_with_a_full_section()
    {
        var values = EnabledValues().Select(v => v.Key.EndsWith(":Enabled", StringComparison.Ordinal) ? (v.Key, "false") : v).ToArray();

        var services = Register(Config(values));

        Assert.Empty(services);
    }

    [Fact]
    public async Task With_FreeLlmApi_disabled_the_options_resolve_to_the_disabled_defaults()
    {
        var services = new ServiceCollection();
        services.AddOptions();
        services.AddBazaarComponents(Config(), [new FreeLlmApiComponent()]);

        await using var provider = services.BuildServiceProvider(validateScopes: true);
        var options = provider.GetService<IOptions<FreeLlmApiOptions>>()?.Value ?? new FreeLlmApiOptions();

        Assert.False(options.Enabled);
        Assert.Equal("auto", options.DefaultModel);
    }

    [Fact]
    public async Task With_FreeLlmApi_enabled_the_component_binds_options_and_registers_the_startup_check_and_keyed_client()
    {
        var services = Register(Config([.. EnabledValues(), ("Bazaar:Gateway:FreeLlmApi:TimeoutSeconds", "45")]));

        await using var provider = services.BuildServiceProvider(validateScopes: true);
        var options = provider.GetRequiredService<IOptions<FreeLlmApiOptions>>().Value;

        Assert.True(options.Enabled);
        Assert.Equal("http://127.0.0.1:3001/v1", options.BaseUrl);
        Assert.Equal(45, options.TimeoutSeconds);
        Assert.Contains(services, d => d.ServiceType == typeof(IHostedService) && d.ImplementationType == typeof(FreeLlmApiStartupCheckService));
        Assert.Equal(45, provider.GetRequiredKeyedService<HttpClient>(FreeLlmApiComponent.HttpClientKey).Timeout.TotalSeconds);
        Assert.IsType<FreeLlmApiRoutingDiagnostics>(provider.GetRequiredService<FreeLlmApiRoutingDiagnostics>());
    }

    [Fact]
    public void A_managed_sidecar_turns_the_component_on_the_same_way_the_options_do()
    {
        var services = Register(Config(
            ("Bazaar:Gateway:FreeLlmApi:Sidecar:Managed", "true")));

        Assert.Contains(services, d => d.ServiceType == typeof(IHostedService) && d.ImplementationType == typeof(FreeLlmApiStartupCheckService));
    }

    [Theory]
    [InlineData("ftp://127.0.0.1/v1")]
    [InlineData("127.0.0.1:3001/v1")]
    public async Task With_FreeLlmApi_enabled_a_bad_base_url_fails_validation_with_the_same_message(string baseUrl)
    {
        var services = Register(Config(EnabledValues(baseUrl)));

        await using var provider = services.BuildServiceProvider(validateScopes: true);
        var error = Assert.Throws<OptionsValidationException>(() => provider.GetRequiredService<IOptions<FreeLlmApiOptions>>().Value);

        Assert.Contains("Bazaar:Gateway:FreeLlmApi:BaseUrl must be an absolute http or https URL.", error.Failures);
    }
}
