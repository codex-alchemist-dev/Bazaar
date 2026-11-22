using System.Net;
using Bazaar.Conduit.Settings;
using Bazaar.Conduit.Setup;
using Bazaar.Core;
using Bazaar.Server.Settings;
using Bazaar.Server.Setup;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Bazaar.Conduit;

/// <summary>
/// Conduit (docs/design/39-conduit-remote-connectivity.md) as a Bazaar Component. It contributes the Conduit settings keys
/// (<see cref="ConduitSettingContribution"/>), so the setup plan, the wizard and the admin panel know them. Registrations only: no
/// I/O, and no module starts here. Every exposing module stays off until the owner approves it.
/// </summary>
public sealed class ConduitComponent : IBazaarComponent
{
    public static ComponentManifest ManifestDefinition { get; } = new(
        Id: "conduit",
        Version: "0.1.0",
        Kind: ComponentKind.Feature,
        DependsOn: [],
        Provides: ["feature:conduit"]);

    public ComponentManifest Manifest => ManifestDefinition;

    public void AddServices(IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        services.AddSingleton<ISettingContribution, ConduitSettingContribution>();

        // The wizard's remote access check replaces the placeholder with the same id (doc 45 step 15b). It makes a real HTTPS request only
        // when the plan turns a named Cloudflare tunnel on.
        services.TryAddSingleton(TimeProvider.System);
        services.AddSingleton<ISetupVerificationCheck>(sp => new RemoteReachabilityCheck(FetchStatusAsync, sp.GetRequiredService<TimeProvider>()));
    }

    private static readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(10) };

    private static async Task<HttpStatusCode?> FetchStatusAsync(Uri uri, CancellationToken cancellationToken)
    {
        try
        {
            using var response = await _http.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
            return response.StatusCode;
        }
        catch (HttpRequestException)
        {
            return null;
        }
        catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return null;
        }
    }
}
