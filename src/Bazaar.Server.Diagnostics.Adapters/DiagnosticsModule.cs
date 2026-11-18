using Bazaar.Diagnostics.Ingestion;
using Bazaar.Diagnostics.Redaction;
using Bazaar.Server.Components.Erasure;
using Bazaar.Server.Diagnostics.Persistence;
using Bazaar.Server.Gc.Body;
using Bazaar.Server.Security.Audit;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Bazaar.Server.Diagnostics.Adapters;

/// <summary>Builds the intake service for one authenticated connection, which is what decides install ownership.</summary>
public sealed class CrashIntakeFactory(ICrashIntakeStore store, ICrashPayloadSealer sealer, IAuditLog audit, ISymbolicator symbolicator, TimeProvider clock, CrashFilingOptions options)
{
    /// <summary>Creates the service for a connection whose devices own the installs <paramref name="ownership"/> accepts.</summary>
    public CrashIntakeService Create(IInstallOwnershipResolver ownership) =>
        new(new CrashIngestionPipeline(ownership, new Redactor(), symbolicator), store, sealer, audit, clock, options);
}

/// <summary>The symbol lookup used until the release symbol bundles of doc 37 exist: every report is kept with symbols marked missing.</summary>
public sealed class NoSymbolsSymbolicator : ISymbolicator
{
    /// <inheritdoc />
    public SymbolsState Resolve(Bazaar.Contracts.Telemetry.CrashReport report) => SymbolsState.Missing;
}

/// <summary>Composition of the crash diagnostics module. The host calls it once.</summary>
public static class DiagnosticsModule
{
    /// <summary>The configuration section the filing options are read from.</summary>
    public const string ConfigurationSection = "Diagnostics:Crash";

    /// <summary>
    /// Registers the store, sealer, intake factory, dispatcher, erasure participant and maintenance service. Expects the shared
    /// <c>NpgsqlDataSource</c>, <see cref="PersonKeyStore"/> and <see cref="IAuditLog"/> to be registered. The GitHub filer and the
    /// install directory are seams: until the host registers them, filing stays off and erasure fails closed.
    /// </summary>
    public static IServiceCollection AddBazaarDiagnosticsModule(this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        var section = configuration.GetSection(ConfigurationSection);
        var options = new CrashFilingOptions
        {
            IssueRepo = section["IssueRepo"] ?? string.Empty,
            IssuesPerDay = section.GetValue("IssuesPerDay", 20),
        };
        services.TryAddSingleton(options);
        services.TryAddSingleton(CrashRetentionPolicy.Default);
        services.TryAddSingleton(CrashSealingOptions.Default);
        services.TryAddSingleton<ICrashInstallDirectory, UnavailableCrashInstallDirectory>();
        services.TryAddSingleton<ISymbolicator, NoSymbolsSymbolicator>();
        services.TryAddSingleton(TimeProvider.System);

        services.TryAddSingleton<PersonKeyStore>();
        services.TryAddSingleton<PostgresCrashStore>();
        services.TryAddSingleton<ICrashIntakeStore>(sp => sp.GetRequiredService<PostgresCrashStore>());
        services.TryAddSingleton<ICrashActionOutbox>(sp => sp.GetRequiredService<PostgresCrashStore>());
        services.TryAddSingleton<ICrashRetention>(sp => sp.GetRequiredService<PostgresCrashStore>());
        services.TryAddSingleton<ICrashErasure>(sp => sp.GetRequiredService<PostgresCrashStore>());
        services.TryAddSingleton<ICrashPayloadSealer, PersonKeyCrashSealer>();
        services.TryAddSingleton<CrashIntakeFactory>();
        services.TryAddSingleton<CrashActionDispatcher>();
        services.AddSingleton<IErasureParticipant, CrashErasureParticipant>();
        services.AddHostedService<CrashMaintenanceService>();
        return services;
    }
}
