using Bazaar.Server.GitHub.Ingress;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace Bazaar.Server.GitHub.Pr;

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
    /// the creator principal (owner, else bridge), the database, or both an <see cref="IGitHubPrGcFactory"/> and an <c>IGcProvisioner</c> are missing.
    /// </summary>
    internal static IGitHubEventSink Decorate(IServiceProvider sp, IGitHubEventSink inner)
    {
        var prOptions = sp.GetRequiredService<GitHubPrGcOptions>();
        Guid? bridge = sp.GetRequiredService<GitHubIngressOptions>().EffectiveBridgePrincipalId;
        if ((prOptions.OwnerPrincipalId ?? bridge) is not { } creator || sp.GetService<NpgsqlDataSource>() is not { } dataSource
            || (sp.GetService<IGitHubPrGcFactory>() ?? FromProvisioner(sp, prOptions)) is not { } factory)
            return inner;
        var service = new GitHubPrService(
            dataSource, factory, sp.GetRequiredService<IGitHubGcDirectory>(),
            prOptions, creator, bridge, sp.GetRequiredService<ILogger<GitHubPrService>>(),
            sp.GetService<IGitHubOwnerResolver>() ?? new PgGitHubOwnerResolver(dataSource));
        return new GitHubPrGcEventSink(inner, service);
    }

    // The real factory exists whenever the GC module's provisioner does (doc 40, GitHub PR GC creation); a registered factory wins, so tests can substitute one.
    private static GcProvisionerPrGcFactory? FromProvisioner(IServiceProvider sp, GitHubPrGcOptions prOptions) =>
        sp.GetService<Bazaar.Server.Gc.Write.IGcProvisioner>() is { } provisioner
            ? new GcProvisionerPrGcFactory(provisioner, prOptions, sp.GetRequiredService<GitHubIngressOptions>())
            : null;
}
