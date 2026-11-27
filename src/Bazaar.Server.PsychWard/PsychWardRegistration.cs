using Bazaar.LoopGuard.Abstractions;
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
        services.TryAddSingleton<IPsychWardService, PsychWardService>();
        services.TryAddSingleton<IVentService, VentService>();
        services.TryAddSingleton<IPsychWardGate, PsychWardGate>();
        return services;
    }
}
