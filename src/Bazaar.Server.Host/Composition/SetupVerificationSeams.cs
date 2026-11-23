using Bazaar.Server.Data.Migrations;
using Bazaar.Server.Host.Startup;
using Bazaar.Server.Providers;
using Bazaar.Server.Setup.Probes;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Bazaar.Server.Host.Composition;

/// <summary>
/// The server-bound halves of the setup verification probes (doc 45 section 1.12). The probes live in <c>Bazaar.Server.Setup.Probes</c>
/// and read these three seams: the schema state against this host's migration files, the running-instance lock, and the provider report.
/// All three are read-only.
/// </summary>
public static class SetupVerificationSeams
{
    public static IServiceCollection AddSetupVerificationSeams(this IServiceCollection services)
    {
        services.TryAddSingleton<ISchemaStateReader, HostSchemaStateReader>();
        services.TryAddSingleton<IInstanceLockReader>(sp => new HostInstanceLockReader(sp.GetRequiredService<IInstanceLock>()));
        services.TryAddSingleton<IProviderRoleReader>(sp => new HostProviderRoleReader(sp.GetRequiredService<ProviderCapabilityReport>()));
        return services;
    }
}

internal sealed class HostSchemaStateReader(DatabaseConnection database) : ISchemaStateReader
{
    public async Task<SetupSchemaState> ReadAsync(CancellationToken cancellationToken)
    {
        var dataSource = await database.GetAsync(cancellationToken).ConfigureAwait(false);
        var state = await new MigrationRunner(dataSource, components: HostComponents.All.Select(c => c.Manifest))
            .GetStateAsync(HostMigrations.Source(), MigrateCommand.CurrentProvider(), cancellationToken).ConfigureAwait(false);
        return state switch
        {
            SchemaState.Current => SetupSchemaState.Current,
            SchemaState.Ahead => SetupSchemaState.Ahead,
            _ => SetupSchemaState.Behind,
        };
    }
}

internal sealed class HostInstanceLockReader(IInstanceLock instanceLock) : IInstanceLockReader
{
    public bool IsHeld => instanceLock.IsHeld;
}

internal sealed class HostProviderRoleReader(ProviderCapabilityReport report) : IProviderRoleReader
{
    public IReadOnlyList<ProviderRoleReading> Read() =>
    [
        .. report.Entries
            .Where(entry => entry.Role is not null)
            .GroupBy(entry => entry.Role!.Value.SettingName(), StringComparer.Ordinal)
            .Select(group => new ProviderRoleReading(group.Key, group.Count(), group.Count(entry => entry.Status.Usable))),
    ];
}
