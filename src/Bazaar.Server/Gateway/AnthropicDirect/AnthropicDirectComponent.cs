using Bazaar.Server.Components;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Bazaar.Server.Gateway.AnthropicDirect;

/// <summary>
/// The owner's Anthropic API key as a Bazaar Component (docs/design/05-model-gateway.md, "Claude lane"). It owns the
/// <c>Bazaar:Gateway:AnthropicDirect</c> options, their start-time validation and the start-time key check. Shared gateway pieces
/// stay in <c>AddBazaarGateway</c>, which reads these options with the disabled defaults when this component is not registered.
/// </summary>
/// <remarks>When the source is off (the default), nothing is registered: no options binding and no startup key check.</remarks>
public sealed class AnthropicDirectComponent : IBazaarComponent
{
    public static ComponentManifest ManifestDefinition { get; } = new(
        Id: "anthropic-direct",
        Version: "0.1.0",
        Kind: ComponentKind.Provider,
        DependsOn: [],
        Provides: ["gateway:provider:anthropic-direct"]);

    public ComponentManifest Manifest => ManifestDefinition;

    public void AddServices(IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        if (!IsEnabled(configuration))
        {
            return;
        }

        services.AddOptions<AnthropicDirectOptions>()
            .Configure(options => configuration.GetSection(AnthropicDirectOptions.SectionName).Bind(options))
            .ValidateOnStart();
        services.AddSingleton<IValidateOptions<AnthropicDirectOptions>, AnthropicDirectOptionsValidator>();
        services.AddHostedService<AnthropicDirectStartupCheckService>();
    }

    /// <summary>
    /// Fails startup when the source is switched on (<c>Bazaar:Gateway:AnthropicDirect:Enabled</c>) while this component is
    /// disabled (<c>Bazaar:Components:anthropic-direct:Enabled=false</c>). A disabled component registers nothing, so without
    /// this check the gateway setting would be ignored silently.
    /// </summary>
    public static void RequireComponentWhenGatewayIsOn(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        if (IsEnabled(configuration) && !BazaarComponentRegistration.IsEnabled(configuration, ManifestDefinition.Id))
        {
            throw new InvalidOperationException(
                $"{AnthropicDirectOptions.SectionName}:Enabled is true, but the {ManifestDefinition.Id} component is disabled " +
                $"(Bazaar:Components:{ManifestDefinition.Id}:Enabled=false). Enable the component or set {AnthropicDirectOptions.SectionName}:Enabled to false.");
        }
    }

    /// <summary>Whether the source is on. A disabled or unbound section means off, which is the shipped default.</summary>
    public static bool IsEnabled(IConfiguration configuration)
    {
        var probe = new AnthropicDirectOptions();
        configuration.GetSection(AnthropicDirectOptions.SectionName).Bind(probe);
        return probe.Enabled;
    }
}
