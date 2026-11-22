using Bazaar.Kodo.Settings;

namespace Bazaar.Server.Kodo;

/// <summary>
/// The server module's own settings (doc 46 section 5.4, PROPOSED). The probe, state and journal numbers are not here: they come
/// from the doc 46 section 20 catalogue in <see cref="Settings"/>. These are the wiring choices the catalogue does not carry.
/// </summary>
public sealed record KodoServerOptions
{
    /// <summary>Lower bound of <see cref="TickInterval"/>, so a bad value cannot spin the loop.</summary>
    public static TimeSpan MinimumTick { get; } = TimeSpan.FromSeconds(1);

    /// <summary>False runs nothing: no loop, no journal, no probes. The read endpoints answer 404 <c>kodo_disabled</c>. Default true.</summary>
    public bool Enabled { get; init; } = true;

    /// <summary>
    /// Folder for the local journal spill file. Null keeps the journal in memory only, which is the shipped default because no
    /// data directory is decided yet (doc 46 section 5.3 names <c>kodo-data</c> without a path).
    /// </summary>
    public string? DataDirectory { get; init; }

    /// <summary>
    /// Where the latest status snapshot is written after every pass, for the resident TUI (doc 58 section 3), a separate process.
    /// Null writes nothing (the shipped default of the record; <c>AddBazaarKodo</c> with configuration resolves
    /// <c>Bazaar:Kodo:SnapshotFile</c>, then <see cref="DataDirectory"/>, then the machine-wide default, and an explicit empty value turns it off).
    /// </summary>
    public string? SnapshotFile { get; init; }

    /// <summary>The validated section 20 settings. Invalid or loosening operator values fall back to the defaults and are listed in <see cref="SettingErrors"/>.</summary>
    public KodoSettings Settings { get; init; } = KodoSettings.Defaults;

    /// <summary>Operator setting errors found while reading configuration, as <c>key: message</c>. Logged at start.</summary>
    public IReadOnlyList<string> SettingErrors { get; init; } = [];

    /// <summary>How often the loop wakes to run the probes that are due. Not below <see cref="MinimumTick"/>. Default 5 seconds.</summary>
    public TimeSpan TickInterval { get; init; } = TimeSpan.FromSeconds(5);

    /// <summary>Heartbeat cadence for the <c>kodo</c> stream (doc 46 section 15.6: one third of the 30 second silence budget). Default 10 seconds.</summary>
    public TimeSpan HeartbeatInterval { get; init; } = TimeSpan.FromSeconds(10);

    /// <summary>Grace before a non-critical red or amber probe counts against the state (doc 46 section 8.2 gives no value). PROPOSED 2 minutes.</summary>
    public TimeSpan NonCriticalGrace { get; init; } = TimeSpan.FromMinutes(2);

    /// <summary>Journal records kept in memory for the read API. Older records stay in the spill file when one is configured. Default 10000.</summary>
    public int JournalMemoryRecords { get; init; } = 10_000;

    /// <summary>The path whose volume <c>host.disk.free.data</c> reads. It never appears on the wire. Defaults to the application folder.</summary>
    public string DataVolumePath { get; init; } = AppContext.BaseDirectory;

    /// <summary>
    /// Runs <c>host.clock.drift</c>. Default false: no time source is configured, so the probe could only report unknown and
    /// hold the state at degraded forever. OPEN until a clock reading is registered (section 5.4).
    /// </summary>
    public bool IncludeClockDrift { get; init; }
}
