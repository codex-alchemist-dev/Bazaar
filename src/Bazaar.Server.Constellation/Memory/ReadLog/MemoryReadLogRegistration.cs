using Bazaar.Server.Security.Audit;
using Bazaar.Server.Settings;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Npgsql;

namespace Bazaar.Server.Constellation.Memory.ReadLog;

/// <summary>
/// Registers the durable owner-read log (docs/design/constellation-memory-graph.md, "Owner-read access log"): the
/// <c>memory.read_log.*</c> settings, <see cref="IMemoryReadLog"/> and <see cref="IMemoryReadLogReader"/> over Postgres and the
/// hash-chained audit log. <c>MemoryGraphRetriever</c> picks up <see cref="IMemoryReadLog"/> when one is registered; without this call
/// nothing is logged. Resolved at first use: <see cref="NpgsqlDataSource"/> and <see cref="ITransactionalAuditLog"/> are required.
/// </summary>
public static class MemoryReadLogRegistration
{
    public static IServiceCollection AddMemoryReadLog(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.TryAddEnumerable(ServiceDescriptor.Singleton<ISettingContribution, MemoryReadLogSettingContribution>());
        services.TryAddSingleton(sp =>
        {
            ISettingStore? store = sp.GetService<ISettingStore>();
            Func<CancellationToken, Task<int>>? maxIds = store is null
                ? null
                : ct => store.GetAsync(MemoryReadLogSettings.MaxIdsPerRowKey, cancellationToken: ct);
            return new PgMemoryReadLog(
                sp.GetRequiredService<NpgsqlDataSource>(),
                sp.GetRequiredService<ITransactionalAuditLog>(),
                sp.GetService<TimeProvider>(),
                maxIds);
        });
        services.TryAddSingleton<IMemoryReadLog>(sp => sp.GetRequiredService<PgMemoryReadLog>());
        services.TryAddSingleton<IMemoryReadLogReader>(sp => sp.GetRequiredService<PgMemoryReadLog>());
        return services;
    }
}
