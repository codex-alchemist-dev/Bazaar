using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Bazaar.Server.MailDns;

public static class MailDnsServiceCollectionExtensions
{
    /// <summary>
    /// Registers the DNS resolver, the checker and the setup step. A resolver or checker registered earlier is kept, so a host or a
    /// test can substitute its own. <paramref name="configure"/> sets the shipped resolver's timeout and name servers.
    /// </summary>
    public static IServiceCollection AddBazaarMailDns(this IServiceCollection services, Action<DnsResolverOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(services);

        var options = new DnsResolverOptions();
        configure?.Invoke(options);
        services.TryAddSingleton<IDnsResolver>(_ => new DnsClientResolver(options));
        services.TryAddSingleton<IMailDnsChecker>(provider => new MailDnsChecker(provider.GetRequiredService<IDnsResolver>()));
        services.TryAddEnumerable(ServiceDescriptor.Singleton<Bazaar.Server.Setup.ISetupStep, MailDnsSetupStep>());
        return services;
    }
}
