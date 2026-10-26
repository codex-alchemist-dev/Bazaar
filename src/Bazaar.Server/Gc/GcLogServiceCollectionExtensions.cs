using Bazaar.Server.Gc.Read;
using Bazaar.Server.Gc.Write;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Bazaar.Server.Gc;

public static class GcLogServiceCollectionExtensions
{
    /// <summary>
    /// Registers the GC log writer, reader, post-commit notifier, process-wide listener and tail follower. The host must also
    /// register an <c>NpgsqlDataSource</c> and an <see cref="IGcReadAccess"/> (there is no default: viewer rules are not optional).
    /// </summary>
    public static IServiceCollection AddGcLog(this IServiceCollection services)
    {
        services.AddSingleton<CoalescingGcNotifier>();
        services.AddSingleton<IGcNotifier>(sp => sp.GetRequiredService<CoalescingGcNotifier>());
        services.AddSingleton<IHostedService>(sp => sp.GetRequiredService<CoalescingGcNotifier>());
        services.AddSingleton<IGcLogWriter, GcLogWriter>();
        services.AddSingleton<IGcLogReader, GcLogReader>();
        services.AddSingleton<GcNotifyListener>();
        services.AddSingleton<IHostedService>(sp => sp.GetRequiredService<GcNotifyListener>());
        services.AddSingleton<GcTail>();
        return services;
    }

    /// <summary>
    /// Registers the Postgres-backed viewer rules (<see cref="GcReadAccess"/>) as the <see cref="IGcReadAccess"/> the reader
    /// asks before every read. Call it with <see cref="AddGcLog"/>; the reader resolves nothing without it.
    /// </summary>
    public static IServiceCollection AddGcReadAccess(this IServiceCollection services)
    {
        services.AddSingleton<IGcReadAccess, GcReadAccess>();
        return services;
    }
}
