using Bazaar.Client.Tui.Dashboard;
using Bazaar.Core;
using Bazaar.Roles.Abstractions;
using Bazaar.Server.WebAdmin;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Npgsql;

namespace Bazaar.Server.OpsPorts;

/// <summary>
/// The real ports the resident TUI reads (docs/design/58-server-tui.md section 3 and 5, doc 51 audit view). It registers defaults
/// only, with <c>TryAdd</c>, so an owning module's own implementation of a port wins. It needs <see cref="IRoleStore"/> and
/// <see cref="IPermissionChecker"/> (the roles module) for the gate and the roles check, and an <see cref="NpgsqlDataSource"/> for
/// the audit view; a port whose dependency is missing is not registered, and its screen shows "unavailable".
/// <para>The TUI is a separate process from the server, so the status and Kodo ports read the files the server writes
/// (<c>AdminStatusFile</c> from the host, <c>KodoSnapshotFile</c> from the Kodo module); the owning modules implement the same ports
/// in-process for the web panel. Not here: <c>IAdminUserDirectory</c>, <c>IAdminDeviceDirectory</c>, <c>IAdminApprovalSource</c> and
/// <c>IAdminModuleSelection</c>, whose owning modules have no read model yet.</para>
/// </summary>
public sealed class OpsPortsComponent : IBazaarComponent
{
    public static ComponentManifest ManifestDefinition { get; } = new(
        Id: "ops-ports",
        Version: "0.1.0",
        Kind: ComponentKind.Feature,
        DependsOn: ["roles", "audit"],
        Provides: ["feature:ops-ports"]);

    public ComponentManifest Manifest => ManifestDefinition;

    public void AddServices(IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        var options = OpsPortsOptions.FromConfiguration(configuration);
        services.TryAddSingleton(options);
        services.TryAddSingleton<IOpsLogSource>(new FileOpsLogSource(options.LogFile, options.MaxReadBytes));
        services.TryAddSingleton<IOpsEventSource>(new FileOpsEventSource(options.EventFile, options.MaxReadBytes));
        services.TryAddSingleton<IOpsCrashJournal>(new FileOpsCrashJournal(options.CrashJournalFile, options.MaxReadBytes));
        services.TryAddSingleton(new OpsLogFileWriter(options.LogFile));
        services.TryAddSingleton(new OpsEventFileWriter(options.EventFile));
        services.TryAddSingleton<IOpsAuthorizer>(sp => new RolesOpsAuthorizer(sp.GetRequiredService<IPermissionChecker>()));
        services.TryAddSingleton<IAdminStatusSource>(sp => new FileAdminStatusSource(options.StatusFile, sp.GetRequiredService<TimeProvider>()));
        services.TryAddSingleton<IAdminKodoSource>(sp => new FileAdminKodoSource(options.KodoSnapshotFile, sp.GetRequiredService<TimeProvider>()));
        services.TryAddSingleton<IAdminAuditReader>(sp => new PostgresAdminAuditReader(sp.GetRequiredService<NpgsqlDataSource>()));

        // The gate says "on the box" only because the host got here: it holds the operator's database reference (see the class remarks
        // of RoleStoreOwnerLocalGate). A host that cannot prove that passes false by registering its own IOwnerLocalGate first.
        services.TryAddSingleton<RoleStoreOwnerLocalGate>(sp =>
            new RoleStoreOwnerLocalGate(sp.GetRequiredService<IRoleStore>(), () => true, sp.GetRequiredService<OpsPortsOptions>().OwnerPrincipalId));
        services.TryAddSingleton<IOwnerLocalGate>(sp => sp.GetRequiredService<RoleStoreOwnerLocalGate>());
    }
}
