using Bazaar.Wire.Abstractions;
using Grpc.AspNetCore.Server;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;

namespace Bazaar.Server.Transport.Grpc;

/// <summary>Registration of the gRPC and gRPC-Web wire transports (doc 90 section 9).</summary>
public static class GrpcTransportServiceCollectionExtensions
{
    /// <summary>
    /// Adds the transport. The host must also register an <see cref="IWireTransportAuthenticator"/> and an
    /// <see cref="IWireConnectionHandoff"/>; without them the service cannot resolve and every call fails closed.
    /// <see cref="WireTransportsEnabledOptions"/> decides which of <c>grpc</c> and <c>grpc-web</c> run (gRPC-Web is off by default).
    /// </summary>
    public static IServiceCollection AddBazaarGrpcTransport(this IServiceCollection services, Action<GrpcTransportOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(services);
        var options = services.AddOptions<GrpcTransportOptions>();
        if (configure is not null) options.Configure(configure);
        services.AddOptions<WireTransportsEnabledOptions>();
        services.TryAddSingleton<GrpcWebSessionRegistry>();
        services.AddGrpc();
        services.AddOptions<GrpcServiceOptions>().Configure<IOptions<GrpcTransportOptions>>((grpc, transport) =>
        {
            grpc.MaxReceiveMessageSize = transport.Value.MaxMessageBytes;
            grpc.MaxSendMessageSize = transport.Value.MaxMessageBytes;
        });
        services.AddSingleton<IWireTransportListener>(sp => new GrpcTransportListener(
            WireNames.Grpc, sp.GetRequiredService<IOptions<GrpcTransportOptions>>(), sp.GetRequiredService<IOptions<WireTransportsEnabledOptions>>()));
        services.AddSingleton<IWireTransportListener>(sp => new GrpcTransportListener(
            WireNames.GrpcWeb, sp.GetRequiredService<IOptions<GrpcTransportOptions>>(), sp.GetRequiredService<IOptions<WireTransportsEnabledOptions>>()));
        return services;
    }

    /// <summary>
    /// Adds the gRPC-Web translation middleware. Call it before <see cref="MapBazaarGrpcTransport"/>; it only acts on
    /// endpoints that enabled gRPC-Web, so it does nothing while <c>grpc-web</c> is off.
    /// </summary>
    public static IApplicationBuilder UseBazaarGrpcTransport(this IApplicationBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);
        return app.UseGrpcWeb();
    }

    /// <summary>
    /// Maps <c>bazaar.wire.v1.Session</c>. The gRPC-Web protocol is switched on for the endpoint only when
    /// <c>grpc-web</c> is enabled; the service also refuses the web calls per request when it is not.
    /// </summary>
    public static IEndpointConventionBuilder MapBazaarGrpcTransport(this IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);
        var builder = endpoints.MapGrpcService<GrpcSessionService>();
        if (endpoints.ServiceProvider.GetRequiredService<IOptions<WireTransportsEnabledOptions>>().Value.IsEnabled(WireNames.GrpcWeb))
        {
            builder.EnableGrpcWeb();
        }

        return builder;
    }
}
