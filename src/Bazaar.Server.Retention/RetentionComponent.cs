using Cdax.Core;
using Bazaar.Server.Jobs;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Bazaar.Server.Retention;

/// <summary>
/// The retention module (doc 48 section 9): the per-category retention table, the data inventory modules contribute to,
/// and the planner that turns a record's age, soft-delete state and legal hold into one disposition. Periods default to
/// keep; an operator sets a period per category under <c>Bazaar:Retention:{category}:PeriodDays</c> and
/// <c>:SoftDeleteDays</c> with <c>:Method</c> (<c>None</c>, <c>SoftDeleteThenPurge</c> or <c>CryptoErase</c>).
/// </summary>
public sealed class RetentionComponent : ICdaxModule
{
    public static ComponentManifest ManifestDefinition { get; } = new(
        Id: "retention",
        Version: "0.1.0",
        Kind: ComponentKind.Feature,
        DependsOn: [],
        Provides: ["retention:policy", "retention:inventory"]);

    public ComponentManifest Manifest => ManifestDefinition;

    public void AddServices(IServiceCollection services, IConfiguration configuration)
    {
        services.AddSingleton<IRetentionPolicy>(_ => new RetentionPolicyTable(ReadRows(configuration)));
        services.AddSingleton<IRetentionPlanner, RetentionPlanner>();
        services.AddSingleton<IDataInventory, DataInventory>();

        // The sweeper (doc 48 section 9). Stores come from the modules that own the data; with none registered it does nothing.
        // It needs IAuditLog, ILegalHoldScopeReader (Bazaar.Erasure) and the jobs poller from the host.
        services.TryAddSingleton(TimeProvider.System);
        services.AddSingleton(_ => ReadSweepOptions(configuration));
        services.AddSingleton<RetentionSweeper>();
        services.AddSingleton<RetentionSweepJobHandler>();
        services.AddSingleton<IJobHandler>(sp => sp.GetRequiredService<RetentionSweepJobHandler>());
        services.AddSingleton(new RecurringJob(RetentionSweepJobHandler.JobKind, TimeSpan.FromHours(1)));
    }

    public static RetentionSweepOptions ReadSweepOptions(IConfiguration configuration)
    {
        var section = configuration.GetSection("Bazaar:Retention:Sweep");
        var defaults = new RetentionSweepOptions();
        // A bad value falls back to the safe default rather than stopping start-up.
        var size = int.TryParse(section["BatchSize"], out var b) && b is >= 1 and <= 50_000 ? b : defaults.BatchSize;
        var max = int.TryParse(section["MaxBatchesPerStep"], out var m) && m is >= 1 and <= 1_000 ? m : defaults.MaxBatchesPerStep;
        return new RetentionSweepOptions(size, max);
    }

    internal static List<RetentionCategory> ReadRows(IConfiguration configuration)
    {
        var rows = new List<RetentionCategory>();
        foreach (var section in configuration.GetSection("Bazaar:Retention").GetChildren())
        {
            var period = int.TryParse(section["PeriodDays"], out var days) ? TimeSpan.FromDays(days) : (TimeSpan?)null;
            var window = int.TryParse(section["SoftDeleteDays"], out var w) ? TimeSpan.FromDays(w) : TimeSpan.FromDays(30);
            var method = Enum.TryParse<ErasureMethod>(section["Method"], ignoreCase: true, out var m) ? m : ErasureMethod.None;
            rows.Add(new RetentionCategory(section.Key, period, window, method));
        }

        return rows;
    }
}
