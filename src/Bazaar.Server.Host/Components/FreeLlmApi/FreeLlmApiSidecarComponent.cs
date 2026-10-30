using Bazaar.Server.Gateway.FreeLlmApi;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;

namespace Bazaar.Server.Host.Components.FreeLlmApi;

/// <summary>
/// The FreeLLMAPI sidecar as a Bazaar Component. Its one public registration is <see cref="AddBazaarFreeLlmApiSidecar"/>, which only
/// host composition calls. It depends on the gateway's contract types only.
/// </summary>
public static class FreeLlmApiSidecarComponent
{
    /// <summary>Binds <c>Bazaar:Gateway:FreeLlmApi:Sidecar</c>, validates it at start and runs the managed process when enabled.</summary>
    // TODO(component): move this folder to its own project once the component model PR merges.
    public static IServiceCollection AddBazaarFreeLlmApiSidecar(this IServiceCollection services)
    {
        services.AddOptions<FreeLlmApiSidecarOptions>()
            .Configure<IServiceProvider>((options, sp) =>
                sp.GetService<IConfiguration>()?.GetSection(FreeLlmApiSidecarOptions.SectionName).Bind(options))
            .ValidateOnStart();
        services.TryAddSingleton<IValidateOptions<FreeLlmApiSidecarOptions>, FreeLlmApiSidecarOptionsValidator>();
        services.AddHostedService<FreeLlmApiSidecarService>();
        return services;
    }
}
