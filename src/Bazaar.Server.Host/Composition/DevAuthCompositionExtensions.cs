using Bazaar.Server.Secrets;
using Bazaar.Server.Wire.Connection;

namespace Bazaar.Server.Host.Composition;

/// <summary>Local development sign-in (<c>Bazaar:DevAuth</c>). Register before <c>AddBazaarHostWire</c>; without it, DenyAll stays in place.</summary>
public static class DevAuthCompositionExtensions
{
    /// <summary>
    /// Binds <c>Bazaar:DevAuth</c> and, only when <c>Enabled</c> is true, makes <see cref="DevTokenAuthenticator"/> the connection
    /// authenticator. An enabled config with no usable secret fails the start. When disabled, nothing is registered.
    /// </summary>
    public static IServiceCollection AddBazaarDevAuth(this IServiceCollection services, IConfiguration configuration)
    {
        var section = configuration.GetSection(DevAuthOptions.SectionName);
        if (!section.GetValue<bool>(nameof(DevAuthOptions.Enabled)))
        {
            return services;
        }

        services.AddOptions<DevAuthOptions>().Bind(section).ValidateOnStart();
        services.AddSingleton<IValidateOptions<DevAuthOptions>, DevAuthOptionsValidator>();
        services.AddSingleton<IConnectionAuthenticator>(sp =>
        {
            var options = sp.GetRequiredService<IOptions<DevAuthOptions>>().Value;
            if (string.IsNullOrWhiteSpace(options.TokenSecret))
            {
                throw new InvalidOperationException($"{DevAuthOptions.SectionName}:TokenSecret is required when {DevAuthOptions.SectionName}:Enabled is true.");
            }

            var resolver = sp.GetRequiredService<ISecretResolver>();
            if (!resolver.CanResolve(options.TokenSecret))
            {
                throw new InvalidOperationException(
                    $"{DevAuthOptions.SectionName}:TokenSecret uses a scheme this server cannot resolve; use env:NAME or file:PATH.");
            }

            // Read once when the authenticator is built, as the database secret is read at startup.
            var token = resolver.ResolveAsync(options.TokenSecret, CancellationToken.None).AsTask().GetAwaiter().GetResult();
            if (string.IsNullOrWhiteSpace(token))
            {
                throw new InvalidOperationException($"{DevAuthOptions.SectionName}:TokenSecret resolved to an empty value.");
            }

            return new DevTokenAuthenticator(token, options.PrincipalId);
        });
        services.AddHostedService<DevAuthStartupCheck>();
        return services;
    }
}
