using Bazaar.GitHub.Ingress;
using Bazaar.GitHub.Pr;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace Bazaar.GitHub.Issues;

/// <summary>Registration of the issue GC service (doc 21, issue GCs). Every registration is TryAdd.</summary>
public static class GitHubIssueGcRegistration
{
    public static IServiceCollection AddBazaarGitHubIssueGc(this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);
        services.TryAddSingleton(configuration.GetSection(GitHubRegistration.ConfigurationSection).GetSection("IssueGc").Get<GitHubIssueGcOptions>() ?? new GitHubIssueGcOptions());
        return services;
    }

    /// <summary>
    /// Wraps <paramref name="inner"/> so issue GCs exist before events are written to them. Returns <paramref name="inner"/> unchanged when
    /// the creator principal (owner, else bridge), the database, or both an <see cref="IGitHubIssueGcFactory"/> and an <c>IGcProvisioner</c> are missing.
    /// </summary>
    internal static IGitHubEventSink Decorate(IServiceProvider sp, IGitHubEventSink inner)
    {
        var options = sp.GetRequiredService<GitHubIssueGcOptions>();
        Guid? bridge = sp.GetRequiredService<GitHubIngressOptions>().EffectiveBridgePrincipalId;
        Guid? owner = options.OwnerPrincipalId ?? sp.GetRequiredService<GitHubPrGcOptions>().OwnerPrincipalId;
        if ((owner ?? bridge) is not { } creator || sp.GetService<NpgsqlDataSource>() is not { } dataSource
            || (sp.GetService<IGitHubIssueGcFactory>() ?? FromProvisioner(sp, options, owner)) is not { } factory)
            return inner;
        var service = new GitHubIssueService(
            dataSource, factory, sp.GetRequiredService<IGitHubGcDirectory>(), options, creator, bridge, sp.GetRequiredService<ILogger<GitHubIssueService>>());
        return new GitHubIssueGcEventSink(inner, service);
    }

    private static GcProvisionerIssueGcFactory? FromProvisioner(IServiceProvider sp, GitHubIssueGcOptions options, Guid? owner) =>
        sp.GetService<Bazaar.Server.Gc.Write.IGcProvisioner>() is { } provisioner
            ? new GcProvisionerIssueGcFactory(provisioner, options, owner, sp.GetRequiredService<GitHubIngressOptions>())
            : null;
}
