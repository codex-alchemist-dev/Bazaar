using Bazaar.Core;
using Bazaar.Kodo.Probes.Host;
using Bazaar.Kodo.Settings;
using Bazaar.Server.WebAdmin;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;

namespace Bazaar.Server.Kodo;

/// <summary>Bazaar.Server.Kodo as a Bazaar Component (doc 46 section 5.4): the health loop, its journal and the read API behind <see cref="IKodoStatusSource"/>.</summary>
public sealed class KodoServerComponent : IBazaarComponent
{
    /// <summary>Configuration section for the module (doc 46 section 4: <c>Bazaar:Kodo</c>).</summary>
    public const string SectionName = "Bazaar:Kodo";

    public static ComponentManifest ManifestDefinition { get; } = new(
        Id: "kodo",
        Version: "0.1.0",
        Kind: ComponentKind.Feature,
        DependsOn: [],
        Provides: ["feature:kodo-health"]);

    public ComponentManifest Manifest => ManifestDefinition;

    public void AddServices(IServiceCollection services, IConfiguration configuration) => services.AddBazaarKodo(configuration);
}

/// <summary>Composition for Bazaar.Server.Kodo.</summary>
public static class KodoServiceCollectionExtensions
{
    /// <summary>
    /// Registers the Kodo monitor, its hosted loop and <see cref="IKodoStatusSource"/>. Reads <c>Bazaar:Kodo</c>: <c>Enabled</c>,
    /// <c>DataDirectory</c>, <c>TickSeconds</c>, <c>HeartbeatSeconds</c>, <c>NonCriticalGraceSeconds</c>, <c>JournalMemoryRecords</c>,
    /// <c>DataVolumePath</c>, <c>IncludeClockDrift</c> and <c>Settings:&lt;kodo.key&gt;</c> for the section 20 catalogue. Bound by hand, no
    /// reflection. With <c>Enabled</c> false nothing is registered that runs. Every registration is TryAdd, so a host or test can
    /// substitute first (a stream publisher, host readings, a clock).
    /// </summary>
    public static IServiceCollection AddBazaarKodo(this IServiceCollection services, IConfiguration? configuration = null)
    {
        ArgumentNullException.ThrowIfNull(services);

        KodoServerOptions options = ReadOptions(configuration?.GetSection(KodoServerComponent.SectionName));
        services.TryAddSingleton(options);
        services.TryAddSingleton(TimeProvider.System);

        // The web panel's Kodo section reads through this port (doc 51 section 5); the TUI reads the snapshot file instead.
        services.TryAddSingleton<IAdminKodoSource>(sp => new KodoAdminSource(sp.GetRequiredService<IKodoStatusSource>(), sp.GetRequiredService<TimeProvider>()));
        if (!options.Enabled)
        {
            services.TryAddSingleton<IKodoStatusSource, DisabledKodoStatusSource>();
            return services;
        }

        services.TryAddSingleton<IKodoStreamPublisher, NullKodoStreamPublisher>();
        services.TryAddSingleton<IHostReadings, KodoHostReadings>();
        services.TryAddSingleton<KodoMonitor>(sp => new KodoMonitor(
            sp.GetRequiredService<KodoServerOptions>(),
            sp.GetRequiredService<IHostReadings>(),
            sp.GetRequiredService<IKodoStreamPublisher>(),
            sp.GetRequiredService<TimeProvider>(),
            sp.GetRequiredService<ILogger<KodoMonitor>>()));
        services.TryAddSingleton<IKodoStatusSource>(sp => sp.GetRequiredService<KodoMonitor>());
        services.AddHostedService<KodoMonitorService>();
        return services;
    }

    internal static KodoServerOptions ReadOptions(IConfigurationSection? s)
    {
        KodoServerOptions d = new();
        Dictionary<string, string> operatorValues = [];
        foreach (KodoSettingDefinition definition in KodoSettingKey.All)
        {
            if (s?.GetSection("Settings")[definition.Key] is { Length: > 0 } raw)
            {
                operatorValues[definition.Key] = raw;
            }
        }

        // A value that does not parse, or that loosens a floor, is ignored (the default applies) and reported; the rest still apply.
        KodoSettingsResult result = KodoSettingsValidator.Validate(operatorValues);
        return d with
        {
            Enabled = Bool(s, "Enabled", d.Enabled),
            DataDirectory = string.IsNullOrWhiteSpace(s?["DataDirectory"]) ? d.DataDirectory : s["DataDirectory"],
            Settings = result.Settings,
            SettingErrors = result.Errors.Select(e => $"{e.Key}: {e.Message}").ToArray(),
            TickInterval = Seconds(s, "TickSeconds", d.TickInterval),
            HeartbeatInterval = Seconds(s, "HeartbeatSeconds", d.HeartbeatInterval),
            NonCriticalGrace = Seconds(s, "NonCriticalGraceSeconds", d.NonCriticalGrace),
            JournalMemoryRecords = Int(s, "JournalMemoryRecords", d.JournalMemoryRecords),
            DataVolumePath = string.IsNullOrWhiteSpace(s?["DataVolumePath"]) ? d.DataVolumePath : s["DataVolumePath"]!,
            IncludeClockDrift = Bool(s, "IncludeClockDrift", d.IncludeClockDrift),
            SnapshotFile = SnapshotPath(s),
        };
    }

    // With configuration present the file is on by default (the TUI needs it); an explicitly empty value turns it off.
    private static string? SnapshotPath(IConfigurationSection? s)
    {
        if (s is null)
        {
            return null;
        }

        string? explicitPath = s["SnapshotFile"];
        return explicitPath is { Length: 0 } ? null : KodoSnapshotFile.ResolvePath(explicitPath, s["DataDirectory"]);
    }

    private static bool Bool(IConfigurationSection? s, string key, bool fallback) => bool.TryParse(s?[key], out bool v) ? v : fallback;

    private static int Int(IConfigurationSection? s, string key, int fallback) =>
        int.TryParse(s?[key], System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out int v) && v > 0 ? v : fallback;

    private static TimeSpan Seconds(IConfigurationSection? s, string key, TimeSpan fallback) =>
        double.TryParse(s?[key], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double v) && v > 0
            ? TimeSpan.FromSeconds(v)
            : fallback;
}

/// <summary>The status source of a switched-off module: no snapshot, no journal, nothing running.</summary>
internal sealed class DisabledKodoStatusSource : IKodoStatusSource
{
    public bool Enabled => false;

    public Bazaar.Contracts.Kodo.KodoStatusSnapshotDto? Latest => null;

    public IReadOnlyList<KodoJournalEntryView> RecentJournal(int limit) => [];

    public KodoJournalProofView? JournalProof(long sequence) => null;
}
