using Bazaar.Server.Components;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;

namespace Bazaar.Server.Gateway.FreeLlmApi;

/// <summary>
/// FreeLLMAPI as a Bazaar Component (docs/design/BAZAAR-COMPONENTS.md section 11, docs/design/05b-freellmapi-first-class-provider.md).
/// It owns the <c>Bazaar:Gateway:FreeLlmApi</c> options, their start-time validation, the start-time key check, the routing
/// diagnostics and the FreeLLMAPI HTTP client. Shared gateway pieces (pipeline, usage, directory, secrets) stay in
/// <c>AddBazaarGateway</c>, which reads these pieces with the disabled defaults when this component is not registered.
/// </summary>
/// <remarks>
/// When FreeLLMAPI is off (the default), nothing FreeLLMAPI-specific is registered: no options binding, no startup key check
/// and no HTTP client. Enablement is read the same way the options bind it, including the managed-sidecar rule.
/// </remarks>
public sealed class FreeLlmApiComponent : IBazaarComponent
{
    /// <summary>Keyed-service key for the FreeLLMAPI HTTP client, which carries the configured timeout.</summary>
    public const string HttpClientKey = "freellmapi";

    public static ComponentManifest ManifestDefinition { get; } = new(
        Id: "freellmapi",
        Version: "0.1.0",
        Kind: ComponentKind.Provider,
        DependsOn: [],
        Provides: ["gateway:provider:freellmapi"]);

    public ComponentManifest Manifest => ManifestDefinition;

    public void AddServices(IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        if (!IsEnabled(configuration))
        {
            return;
        }

        // Bound from the host configuration, then the managed-sidecar rules. Validation runs on start, as before.
        services.AddOptions<FreeLlmApiOptions>()
            .Configure(options =>
            {
                configuration.GetSection(FreeLlmApiOptions.SectionName).Bind(options);
                options.ApplyManagedSidecarDefaults();
            })
            .ValidateOnStart();
        services.AddSingleton<IValidateOptions<FreeLlmApiOptions>, FreeLlmApiOptionsValidator>();

        // Hosted services start in registration order. The managed sidecar's service is registered in AddBazaarGateway, earlier,
        // so a managed sidecar has written the unified key before this check reads it.
        services.AddHostedService<FreeLlmApiStartupCheckService>();
        services.TryAddSingleton<FreeLlmApiRoutingDiagnostics>();
        services.AddKeyedSingleton<HttpClient>(HttpClientKey, (sp, _) =>
            FreeLlmApiWiring.CreateHttpClient(sp.GetRequiredService<IOptions<FreeLlmApiOptions>>().Value));
    }

    /// <summary>
    /// Fails startup when FreeLLMAPI is switched on (<c>Bazaar:Gateway:FreeLlmApi:Enabled</c>) while this component is disabled
    /// (<c>Bazaar:Components:freellmapi:Enabled=false</c>). A disabled component registers nothing, so without this check the
    /// gateway setting would be ignored silently.
    /// </summary>
    public static void RequireComponentWhenGatewayIsOn(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        if (IsEnabled(configuration) && !BazaarComponentRegistration.IsEnabled(configuration, ManifestDefinition.Id))
        {
            throw new InvalidOperationException(
                "Bazaar:Gateway:FreeLlmApi:Enabled is true, but the freellmapi component is disabled (Bazaar:Components:freellmapi:Enabled=false). " +
                "Enable the component or set Bazaar:Gateway:FreeLlmApi:Enabled to false.");
        }
    }

    /// <summary>
    /// Whether FreeLLMAPI is on: the <c>Enabled</c> flag bound from <c>Bazaar:Gateway:FreeLlmApi</c>, after the managed-sidecar rule.
    /// A disabled or unbound section means off, which is the shipped default.
    /// </summary>
    public static bool IsEnabled(IConfiguration configuration)
    {
        var probe = new FreeLlmApiOptions();
        configuration.GetSection(FreeLlmApiOptions.SectionName).Bind(probe);
        probe.ApplyManagedSidecarDefaults();
        return probe.Enabled;
    }
}
