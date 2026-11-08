using Bazaar.Server.Components;
using Bazaar.Server.Gc.Read;
using Bazaar.Server.Gc.Write;
using Bazaar.Server.Jobs;
using Bazaar.Server.Security.Audit;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace Bazaar.Server.Kanban.Ryusei;

/// <summary>
/// RyuseiFuda schedule (doc 38, Layer 2) as a Bazaar Component. Registers <see cref="RyuseiTriggerService"/> with the caps
/// bound from <see cref="SectionName"/>, and the <see cref="RyuseiTickService"/> behind the <c>ryusei.trigger_tick</c> job.
/// It needs the shared <c>NpgsqlDataSource</c>, <see cref="ITransactionalAuditLog"/> and <see cref="TimeProvider"/>. The event
/// bridge and the agent tools come in later work packages (doc 38, WP-R6 to WP-R9).
/// </summary>
public sealed class RyuseiScheduleComponent : IBazaarComponent
{
    /// <summary>Configuration section for the RyuseiFuda caps (doc 38, Settings).</summary>
    public const string SectionName = "Bazaar:Ryusei";

    public static ComponentManifest ManifestDefinition { get; } = new(
        Id: "ryusei-schedule",
        Version: "0.1.0",
        Kind: ComponentKind.Feature,
        DependsOn: ["kanban"],
        Provides: ["feature:ryusei-schedule"]);

    public ComponentManifest Manifest => ManifestDefinition;

    public void AddServices(IServiceCollection services, IConfiguration configuration)
    {
        var limits = new RyuseiLimits();
        configuration.GetSection(SectionName).Bind(limits);
        services.TryAddSingleton(limits);
        services.TryAddSingleton(sp => new RyuseiTriggerService(
            sp.GetRequiredService<NpgsqlDataSource>(),
            sp.GetRequiredService<ITransactionalAuditLog>(),
            sp.GetRequiredService<RyuseiLimits>()));

        // Scheduler tick (doc 38, WP-R5): the job handler the poller runs for ryusei.trigger_tick.
        // The GC log takes attach pings (WP-R7). Null means no GC log is installed; attach fires then record an error.
        services.TryAddSingleton(sp => new RyuseiTickService(
            sp.GetRequiredService<NpgsqlDataSource>(),
            sp.GetRequiredService<RyuseiLimits>(),
            sp.GetRequiredService<TimeProvider>(),
            gcLog: sp.GetService<IGcLogWriter>()));
        services.TryAddSingleton<RyuseiTickJobHandler>();
        services.AddSingleton<IJobHandler>(sp => sp.GetRequiredService<RyuseiTickJobHandler>());

        // The tick runs every minute (the floor for interval triggers, doc 38). Each bucket adds one job.
        services.AddSingleton(new RecurringJob(RyuseiTickJobHandler.JobKind, TimeSpan.FromMinutes(1)));

        // Event bridge (doc 38, WP-R6): GC and kanban events match event and watcher triggers. Watchers fire on the tick above.
        services.TryAddSingleton(sp => new RyuseiEventBridge(sp.GetRequiredService<NpgsqlDataSource>()));
        services.TryAddSingleton(sp => new RyuseiEventListener(
            sp.GetRequiredService<NpgsqlDataSource>(),
            sp.GetRequiredService<RyuseiEventBridge>(),
            sp.GetRequiredService<GcNotifyListener>(),
            timeProvider: sp.GetRequiredService<TimeProvider>(),
            logger: sp.GetRequiredService<ILogger<RyuseiEventListener>>()));
        services.AddSingleton<IHostedService>(sp => sp.GetRequiredService<RyuseiEventListener>());
    }
}
