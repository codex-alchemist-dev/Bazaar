using Bazaar.GitHub.Ingress;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace Bazaar.GitHub.Pr;

/// <summary>Registration of the PR service (doc 21, work package 6). Every registration is TryAdd.</summary>
public static class GitHubPrGcRegistration
{
    public static IServiceCollection AddBazaarGitHubPrGc(this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);
        services.TryAddSingleton(configuration.GetSection(GitHubRegistration.ConfigurationSection).GetSection("PrGc").Get<GitHubPrGcOptions>() ?? new GitHubPrGcOptions());
        return services;
    }

    /// <summary>
    /// Wraps <paramref name="inner"/> so PR GCs exist before events are written to them. Returns <paramref name="inner"/> unchanged when
    /// the creator principal (owner, else bridge), the database or an <see cref="IGitHubPrGcFactory"/> is missing.
    /// </summary>
    internal static IGitHubEventSink Decorate(IServiceProvider sp, IGitHubEventSink inner)
    {
        var prOptions = sp.GetRequiredService<GitHubPrGcOptions>();
        var bridge = sp.GetRequiredService<GitHubIngressOptions>().BridgePrincipalId;
        if ((prOptions.OwnerPrincipalId ?? bridge) is not { } creator || sp.GetService<NpgsqlDataSource>() is not { } dataSource
            || sp.GetService<IGitHubPrGcFactory>() is not { } factory)
            return inner;
        var service = new GitHubPrService(
            dataSource, factory, sp.GetRequiredService<IGitHubGcDirectory>(),
            prOptions, creator, bridge, sp.GetRequiredService<ILogger<GitHubPrService>>());
        return new GitHubPrGcEventSink(inner, service);
    }
}
