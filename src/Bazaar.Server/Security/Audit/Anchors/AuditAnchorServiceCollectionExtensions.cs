using Bazaar.Server.Security.Audit.Db;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Bazaar.Server.Security.Audit.Anchors;

public static class AuditAnchorServiceCollectionExtensions
{
    /// <summary>
    /// Binds <see cref="AuditAnchorOptions"/> from the <c>AuditAnchor</c> section and validates them. When anchoring is off (the
    /// default), nothing is registered: no store, no service, no target. When it is on, registers <see cref="PostgresAuditAnchorStore"/>
    /// and <see cref="AuditAnchorService"/>; the caller anchors a checkpoint by resolving the service and calling
    /// <see cref="AuditAnchorService.AnchorAsync"/> after the checkpoint is written. This adds no background loop.
    /// </summary>
    /// <remarks>
    /// Needs <see cref="IAuditLog"/> and <see cref="NpgsqlDataSource"/> from the audit component. Enabled anchoring with no enabled
    /// target, or with an unconfigured target, throws here, so the host refuses to start.
    /// </remarks>
    public static IServiceCollection AddAuditAnchoring(this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        var options = configuration.GetSection(AuditAnchorOptions.SectionName).Get<AuditAnchorOptions>() ?? new AuditAnchorOptions();
        options.Validate();
        if (!options.Enabled)
        {
            return services;
        }

        services.TryAddSingleton(TimeProvider.System);
        services.TryAddSingleton<IGitRunner, ProcessGitRunner>();
        services.TryAddSingleton(_ => new HttpClient());
        services.TryAddSingleton<IAuditAnchorStore, PostgresAuditAnchorStore>();
        services.TryAddSingleton<IAuditAnchorFailureLog, PostgresAuditAnchorFailureLog>();
        services.TryAddSingleton(sp => new AuditAnchorService(
            options.CreateTargets(sp.GetRequiredService<HttpClient>(), sp.GetRequiredService<IGitRunner>(), sp.GetRequiredService<TimeProvider>()),
            sp.GetRequiredService<IAuditAnchorStore>(),
            sp.GetRequiredService<IAuditLog>(),
            sp.GetRequiredService<IAuditAnchorFailureLog>(),
            sp.GetRequiredService<TimeProvider>(),
            options.TargetTimeout));
        return services;
    }
}
