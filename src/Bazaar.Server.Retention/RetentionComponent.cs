using Bazaar.Core;
using Bazaar.Server.Jobs;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Bazaar.Server.Retention;

/// <summary>
/// Retention sweeper (doc 48 section 9). Registers the sweeper, its hourly recurring job and the categories the docs fully
/// specify (today only gateway usage). Needs <c>ILegalHoldReader</c> and <c>ILegalHoldScopeReader</c> (Bazaar.Erasure), an
/// <c>IAuditLog</c>, <c>ISettingStore</c> and an <c>NpgsqlDataSource</c> from the host.
/// </summary>
public sealed class RetentionComponent : IBazaarComponent
{
    public static ComponentManifest ManifestDefinition { get; } = new(
        Id: "retention",
        Version: "0.1.0",
        Kind: ComponentKind.Feature,
        DependsOn: [],
        Provides: ["feature:retention"]);

    public ComponentManifest Manifest => ManifestDefinition;

    public void AddServices(IServiceCollection services, IConfiguration configuration)
    {
        services.AddSingleton<IRetentionCategory, GatewayUsageRetentionCategory>();
        services.TryAddSingleton<RetentionSweeper>();
        services.TryAddSingleton<RetentionSweepJobHandler>();
        services.AddSingleton<IJobHandler>(sp => sp.GetRequiredService<RetentionSweepJobHandler>());
        services.AddSingleton(new RecurringJob(RetentionSweepJobHandler.JobKind, TimeSpan.FromHours(1)));
    }
}
