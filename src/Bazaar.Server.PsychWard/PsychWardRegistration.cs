using Bazaar.LoopGuard.Abstractions;
using Bazaar.Server.Gc.Write;
using Bazaar.Server.PsychWard.Abstractions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Npgsql;

namespace Bazaar.Server.PsychWard;

/// <summary>Composition of the Psych Ward module.</summary>
public static class PsychWardRegistration
{
    /// <summary>
    /// Registers the ward, vents and the loop-guard hand-off. Call before the loop-guard module's registration so this
    /// <see cref="IPsychWardGate"/> wins over its no-op default. Settings default to doc 26's shipped values; the host may
    /// register its own <see cref="PsychWardSettings"/> first. The host must register an <c>ITextSanitizer</c>. Stores are
    /// Postgres when an <c>NpgsqlDataSource</c> is registered (migrations in <c>Sql/components/psych_ward</c>), otherwise in-memory.
    /// Ward and vent GC entries are written when the host registers an <c>IGcProvisioner</c>, an <c>IGcLogWriter</c> and and an
    /// <c>IWardPrincipalDirectory</c> (or an <c>IWardOwnerSource</c> with a database, which builds one); otherwise the journal writes nothing.
    /// </summary>
    public static IServiceCollection AddBazaarPsychWard(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.TryAddSingleton(TimeProvider.System);
        services.TryAddSingleton(new PsychWardSettings());
        services.TryAddSingleton<IWardStayStore>(sp =>
            sp.GetService<NpgsqlDataSource>() is { } dataSource ? new PgWardStayStore(dataSource) : new InMemoryWardStayStore());
        services.TryAddSingleton<IVentStore>(sp =>
            sp.GetService<NpgsqlDataSource>() is { } dataSource ? new PgVentStore(dataSource) : new InMemoryVentStore());
        services.TryAddSingleton<IWardOwnerSource>(sp =>
            sp.GetService<NpgsqlDataSource>() is { } dataSource ? new PgWardOwnerSource(dataSource) : new NoWardOwnerSource());
        services.TryAddSingleton<IWardPrincipalDirectory>(sp =>
            sp.GetService<NpgsqlDataSource>() is { } dataSource && sp.GetRequiredService<IWardOwnerSource>() is { } owners and not NoWardOwnerSource
                ? new PgWardPrincipalDirectory(dataSource, owners)
                : new NoWardPrincipalDirectory());
        services.TryAddSingleton<IWardJournal>(sp =>
            sp.GetService<IGcProvisioner>() is { } provisioner
            && sp.GetService<IGcLogWriter>() is { } writer
            && sp.GetRequiredService<IWardPrincipalDirectory>() is { } directory and not NoWardPrincipalDirectory
                ? new GcWardJournal(provisioner, writer, directory)
                : new NoOpWardJournal());
        services.TryAddSingleton<IPsychWardService, PsychWardService>();
        services.TryAddSingleton<IVentService, VentService>();
        services.TryAddSingleton<IWardTriggerIntake, WardTriggerIntake>();
        services.TryAddSingleton<IPsychWardGate, PsychWardGate>();
        return services;
    }
}
