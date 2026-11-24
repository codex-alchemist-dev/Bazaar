using Bazaar.Server.Setup.Probes;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace Bazaar.Server.Setup.Host;

/// <summary>
/// The real probes this host supplies for the verification checks (doc 45 section 1.12), built from what the container holds. A probe whose
/// backing service is not registered (no database, no schema reader) is left out, so its check stays not-evaluated and never green. A probe
/// a module registered itself for the same check id wins over the one built here.
/// </summary>
public static class SetupVerificationProbeSet
{
    /// <summary>The probes for this container: registered ones first, then the defaults for every id they do not answer.</summary>
    public static IReadOnlyList<ISetupInfrastructureProbe> Build(IServiceProvider services)
    {
        ArgumentNullException.ThrowIfNull(services);
        var probes = new List<ISetupInfrastructureProbe>(services.GetServices<ISetupInfrastructureProbe>());
        var answered = probes.Select(probe => probe.CheckId).ToHashSet(StringComparer.Ordinal);
        foreach (var probe in Defaults(services))
        {
            if (answered.Add(probe.CheckId))
            {
                probes.Add(probe);
            }
        }

        return probes;
    }

    private static IEnumerable<ISetupInfrastructureProbe> Defaults(IServiceProvider services)
    {
        var facts = services.GetService<IHostFacts>() ?? new RuntimeHostFacts();
        yield return new HostOsProbe(facts);
        yield return new HostPortsProbe(facts);
        yield return new HostDataRootProbe(facts);
        yield return new HostDiskProbe(facts, services.GetService<Bazaar.Kodo.Probes.Host.IHostReadings>() ?? new DriveInfoHostReadings());
        yield return new BackupTargetReachableProbe();
        yield return new SecretsResolutionProbe();

        var reachability = services.GetService<IReachabilityClient>() ?? new HttpReachabilityClient();
        yield return new AuditAnchorReachableProbe(reachability);
        yield return new ModelSourcesReachableProbe(services.GetService<IModelSourceCatalogue>() ?? ModelSourceCatalogue.Default, reachability);

        var postgres = services.GetService<IPostgresServerReader>()
            ?? (services.GetService<NpgsqlDataSource>() is { } dataSource ? new NpgsqlPostgresServerReader(dataSource) : null);
        if (postgres is not null)
        {
            yield return new PostgresHealthProbe(postgres);
        }

        if (services.GetService<ISchemaStateReader>() is { } schema)
        {
            yield return new SchemaMigrationsProbe(schema);
        }

        if (services.GetService<IInstanceLockReader>() is { } instanceLock)
        {
            yield return new InstanceLockProbe(instanceLock);
        }

        if (services.GetService<IProviderRoleReader>() is { } roles)
        {
            yield return new ProviderRolesProbe(roles);
        }
    }
}
