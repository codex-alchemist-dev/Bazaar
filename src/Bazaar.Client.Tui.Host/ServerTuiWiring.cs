using Bazaar.Admin.Composition;
using Bazaar.Client.Tui.Dashboard;
using Bazaar.Roles;
using Bazaar.Roles.Abstractions;
using Bazaar.PrivacyAudit;
using Bazaar.Server.Components.Remote;
using Bazaar.Server.CompanyBuild.Postgres;
using Bazaar.Server.OpsPorts;
using Bazaar.Server.SignIn.Postgres;
using Bazaar.Server.Security;
using Bazaar.Server.Security.Approvals;
using Bazaar.Server.Security.Approvals.Db;
using Bazaar.Server.Security.Audit;
using Bazaar.Server.WebAdmin;
using Bazaar.Server.WebAdmin.Security;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace Bazaar.Client.Tui.Host;

/// <summary>
/// The real wiring, the program's composition root. The database reference is resolved the way <c>bazaar-admin</c> resolves it
/// (<see cref="PostgresApplyGate"/>), so the operator's own account on the box is what opens the database: that is the local owner
/// gate of doc 45 section 1.9. The audit writer is the server's hash-chained one, taken from the apply gate's registrations, so a TUI
/// sign-in and a setup apply write the same audit log. Ports whose data has no read model yet are left out and their screens show
/// "unavailable" (see <see cref="OpsPortsComponent"/>).
/// </summary>
public sealed class ServerTuiWiring : ITuiHostWiring
{
    public async Task<InstallWiring> OpenInstallAsync(IReadOnlyDictionary<string, string?> variables, CancellationToken ct)
    {
        var gate = await PostgresApplyGate.FromEnvironmentAsync(variables, ct).ConfigureAwait(false);
        return new InstallWiring(gate, gate);
    }

    public async Task<OpsWiring?> OpenOpsAsync(IReadOnlyDictionary<string, string?> variables, CancellationToken ct)
    {
        var gate = await PostgresApplyGate.FromEnvironmentAsync(variables, ct).ConfigureAwait(false);
        if (!gate.DurableAuditStoreAvailable)
        {
            await gate.DisposeAsync().ConfigureAwait(false);
            return null;
        }

        // The gate registers the data source and the audit log; ask it for those two and nothing else.
        var apply = new ServiceCollection();
        gate.RegisterApplyServices(apply);
        var applyProvider = apply.BuildServiceProvider();
        var owner = new Disposables(applyProvider, gate);
        try
        {
            var composed = Compose(
                applyProvider.GetRequiredService<NpgsqlDataSource>(),
                applyProvider.GetRequiredService<IAuditLog>(),
                ConfigurationFrom(variables),
                TimeProvider.System,
                owner);
            return composed;
        }
        catch
        {
            await owner.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    /// <summary>Builds the Ops pieces over a data source and an audit log. Public so the graph is tested without a database.</summary>
    public static OpsWiring Compose(
        NpgsqlDataSource dataSource,
        IAuditLog audit,
        IConfiguration configuration,
        TimeProvider clock,
        IAsyncDisposable? owner = null)
    {
        var services = new ServiceCollection();
        services.AddSingleton(dataSource);
        services.AddSingleton(audit);
        services.AddSingleton(clock);
        services.AddBazaarRoles();
        services.AddSingleton<IAdminAudit>(sp => new AdminAuditRecorder(sp.GetRequiredService<IAuditLog>(), sp.GetRequiredService<TimeProvider>()));

        // The panel's session store, in this process, with the floors of doc 51 (sliding 15 minutes, absolute 8 hours): a TUI session is its
        // own, and the roles check still decides per sign-in. The TUI adds no setting that could loosen the floors.
        services.AddSingleton<IAdminRevocationCheck, NoRevocation>();
        services.AddSingleton<IAdminSessionStore>(sp =>
            new InMemoryAdminSessionStore(AdminSessionOptions.Default, sp.GetRequiredService<IAdminRevocationCheck>()));
        new OpsPortsComponent().AddServices(services, configuration);

        // The identity and approvals ports, from the modules that own that data (docs 32 and 15): the same read ports the web admin uses,
        // and the device revoker over the token service. The token service is built revoke-only: its access-token issuer always refuses.
        services.AddBazaarPrivacyAudit();
        services.AddBazaarRemoteRevokeOnly();
        services.AddBazaarRemoteAdminPorts();
        services.AddSingleton<IApprovalStore>(sp => new PostgresApprovalStore(sp.GetRequiredService<NpgsqlDataSource>()));
        services.AddBazaarAdminApprovalSource();
        // Module selection and the rebuild queue (doc 37 section 4a): the same Postgres store the server uses; the TUI only reads the catalogue.
        services.AddBazaarModuleSelectionPostgres();

        var provider = services.BuildServiceProvider();
        var gate = provider.GetRequiredService<RoleStoreOwnerLocalGate>();
        var signIn = new OpsSignIn(
            gate,
            // The admin-code door: the shared Postgres handoff store the server issues into (doc 32 section 4, doc 51 Session; PROPOSED).
            // Fail closed: when the table is not there yet (an older server) or the database errors, a code is simply refused.
            redeemer: new FailClosedRedeemer(new PostgresAdminHandoffRedeemer(new PostgresHandoffCodeStore(dataSource), clock)),
            provider.GetRequiredService<IAdminSessionStore>(),
            provider.GetRequiredService<IOpsAuthorizer>(),
            provider.GetRequiredService<IAdminAudit>(),
            clock);
        var ports = new OpsPorts(
            Status: provider.GetRequiredService<IAdminStatusSource>(),
            Kodo: provider.GetRequiredService<IAdminKodoSource>(),
            Events: provider.GetRequiredService<IOpsEventSource>(),
            Crashes: provider.GetRequiredService<IOpsCrashJournal>(),
            Logs: provider.GetRequiredService<IOpsLogSource>(),
            Audit: provider.GetRequiredService<IAdminAuditReader>(),
            Users: provider.GetService<IAdminUserDirectory>(),
            Devices: provider.GetService<IAdminDeviceDirectory>(),
            Approvals: provider.GetService<IAdminApprovalSource>(),
            Modules: provider.GetService<IAdminModuleSelection>());
        // Writes with an owning operation: device revoke, suspend and unsuspend a person (identity, doc 32), and module selection
        // (doc 37 section 4a). Not wired, and why: IOpsApprovalDecider (a decision needs a signed owner approval and the step-up proof
        // format is undefined) and IOpsStepUp (needs an approval-key device, so approving from the terminal tells the operator to use
        // the owner device). See doc 58 4a.
        var writes = new OpsWritePorts(
            Members: provider.GetService<IOpsMemberRevoker>(),
            Modules: provider.GetService<IAdminModuleSelection>(),
            Devices: provider.GetService<IOpsDeviceRevoker>(),
            Restorer: provider.GetService<IOpsMemberRestorer>());
        return new OpsWiring(
            ports,
            signIn,
            provider.GetRequiredService<IOpsCrashJournal>(),
            gate.OwnerExistsAsync,
            new Disposables(provider, owner),
            writes);
    }

    /// <summary>The process variables as configuration: <c>Bazaar__Ops__LogFile</c> (or its uppercase form) becomes <c>Bazaar:Ops:LogFile</c>.</summary>
    public static IConfiguration ConfigurationFrom(IReadOnlyDictionary<string, string?> variables)
    {
        ArgumentNullException.ThrowIfNull(variables);
        var values = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        foreach (var (name, value) in variables)
        {
            if (name.StartsWith("BAZAAR__", StringComparison.OrdinalIgnoreCase))
            {
                values[name.Replace("__", ":", StringComparison.Ordinal)] = value;
            }
        }

        return new ConfigurationBuilder().AddInMemoryCollection(values).Build();
    }

    /// <summary>Turns a database failure while redeeming into a refusal, so the admin-code door never throws into the sign-in flow.</summary>
    private sealed class FailClosedRedeemer(IAdminHandoffRedeemer inner) : IAdminHandoffRedeemer
    {
        public async Task<AdminIdentity?> RedeemAsync(string code, CancellationToken ct = default)
        {
            try
            {
                return await inner.RedeemAsync(code, ct).ConfigureAwait(false);
            }
            catch (NpgsqlException)
            {
                return null;
            }
        }
    }

    private sealed class Disposables(params IAsyncDisposable?[] items) : IAsyncDisposable
    {
        public async ValueTask DisposeAsync()
        {
            foreach (var item in items)
            {
                if (item is not null)
                {
                    await item.DisposeAsync().ConfigureAwait(false);
                }
            }
        }
    }
}
