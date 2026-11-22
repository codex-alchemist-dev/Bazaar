using Bazaar.Contracts.Wire.Codec;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;

namespace Bazaar.Server.Wire.Connection;

/// <summary>Registration of the wire connection module (doc 02 WP4).</summary>
public static class WireServiceCollectionExtensions
{
    /// <summary>
    /// Adds the connection manager, idempotent request layer and JSON codec. The host registers an
    /// <see cref="IConnectionAuthenticator"/> and any <see cref="IWireRequestHandler"/>s.
    /// </summary>
    public static IServiceCollection AddBazaarWire(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddOptions<WireOptions>()
            .Bind(configuration.GetSection(WireOptions.SectionName))
            .ValidateOnStart();
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IValidateOptions<WireOptions>, WireOptionsValidator>());
        services.TryAddSingleton(TimeProvider.System);
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IWireCodec, JsonWireCodec>());
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IWireRequestHandler, PingHandler>());
        services.TryAddSingleton<WireConnectionRegistry>();
        services.TryAddSingleton<IdempotentRequestExecutor>();
        services.TryAddSingleton<WireConnectionManager>();
        return services;
    }

    /// <summary>Enables WebSockets and maps the socket endpoint at <see cref="WireOptions.Path"/>.</summary>
    public static WebApplication UseBazaarWire(this WebApplication app)
    {
        var options = app.Services.GetRequiredService<IOptions<WireOptions>>().Value;
        app.UseWebSockets();
        app.MapGet(options.Path, async (HttpContext http, WireConnectionManager manager) =>
        {
            if (!http.WebSockets.IsWebSocketRequest)
            {
                http.Response.StatusCode = StatusCodes.Status400BadRequest;
                return;
            }

            // Credentials never travel in clear text off the machine unless the operator allowed an overlay network.
            var remote = http.Connection.RemoteIpAddress;
            if (!http.Request.IsHttps && !options.AllowPlaintextOverlay && remote is not null && !System.Net.IPAddress.IsLoopback(remote))
            {
                http.Response.StatusCode = StatusCodes.Status403Forbidden;
                return;
            }

            var header = http.Request.Headers.Authorization.ToString();
            var credential = header.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase) ? header[7..].Trim() : null;
            using var socket = await http.WebSockets.AcceptWebSocketAsync();
            await manager.RunAsync(socket, credential, http.RequestAborted);
        });
        return app;
    }
}
