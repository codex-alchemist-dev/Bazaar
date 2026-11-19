using Bazaar.Server.Providers;
using Bazaar.Server.Providers.Search.PgFts;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using Npgsql;

namespace Bazaar.Server.Http;

/// <summary>Registration of the authenticated HTTP surface (doc 02 WP10).</summary>
public static class HttpSurfaceServiceCollectionExtensions
{
    /// <summary>
    /// Adds options, the HTTP rate bucket and the search provider. The host supplies the
    /// <see cref="Wire.Connection.IConnectionAuthenticator"/> and registers the blob module (<c>AddBazaarBlobs</c>, the
    /// <c>Bazaar.Server.Blobs</c> project), whose <c>IBlobStore</c> and <c>IBlobReadAccess</c> the blob routes resolve.
    /// </summary>
    public static IServiceCollection AddBazaarHttpSurface(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddOptions<HttpSurfaceOptions>()
            .Bind(configuration.GetSection(HttpSurfaceOptions.SectionName))
            .ValidateOnStart();
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IValidateOptions<HttpSurfaceOptions>, HttpSurfaceOptionsValidator>());
        services.TryAddSingleton(TimeProvider.System);
        services.TryAddSingleton(sp => sp.GetRequiredService<IOptions<HttpSurfaceOptions>>().Value);
        services.TryAddSingleton<HttpRateLimiter>();
        services.TryAddSingleton<ISearchProvider>(sp => new PgFtsSearchProvider(sp.GetRequiredService<NpgsqlDataSource>()));
        return services;
    }
}
