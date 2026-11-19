using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Bazaar.Server.MailOutbound;

public static class MailOutboundServiceCollectionExtensions
{
    /// <summary>
    /// Registers the outbound send policy. The ports (address directory, approved recipients, settings) and the shared
    /// <c>IRateLimiter</c> come from the host; a policy registered earlier is kept, so a test can substitute its own.
    /// The outbound pipeline is registered too. The host supplies its ports: <see cref="IOutboundMailStore"/> (the Postgres store, which needs
    /// <see cref="IMailBodyProtector"/>), <see cref="IMailApprovalGateway"/>, <see cref="IMailBackendResolver"/> and <c>IAuditLog</c>.
    /// </summary>
    public static IServiceCollection AddBazaarMailOutbound(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.TryAddSingleton<IOutboundMailPolicy, OutboundMailPolicy>();
        services.TryAddSingleton(new OutboundPipelineOptions());
        services.TryAddSingleton<IOutboundMailPipeline>(sp => new OutboundMailPipeline(
            sp.GetRequiredService<IOutboundMailPolicy>(),
            sp.GetRequiredService<IOutboundMailStore>(),
            sp.GetRequiredService<IMailApprovalGateway>(),
            sp.GetRequiredService<IMailBackendResolver>(),
            sp.GetRequiredService<Bazaar.Server.Security.Audit.IAuditLog>(),
            sp.GetRequiredService<OutboundPipelineOptions>(),
            sp.GetService<TimeProvider>()));
        return services;
    }
}
