using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace Bazaar.Server.Hosting;

public static class BazaarHostServiceCollectionExtensions
{
    // TODO(component): register from host composition when the component model (PR #125) merges.
    // AddBazaarTls (Components/Tls) goes here, fed by TlsOptions from the tls.certificate_source setting and the PEM or PFX paths.

    /// <summary>
    /// Binds <see cref="BazaarHostOptions"/> from the <c>Bazaar:Host</c> section, validates it on start and logs it once.
    /// A bad configuration fails the host start with the failing keys in the message. Does not register anything in <c>Program.cs</c>.
    /// </summary>
    public static IServiceCollection AddBazaarHostOptions(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddOptions<BazaarHostOptions>()
            .Bind(configuration.GetSection(BazaarHostOptions.SectionName))
            .ValidateOnStart();
        services.AddSingleton<IValidateOptions<BazaarHostOptions>, BazaarHostOptionsValidator>();
        services.AddHostedService<BazaarHostOptionsLogger>();
        return services;
    }
}
