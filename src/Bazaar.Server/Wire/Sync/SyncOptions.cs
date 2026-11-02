namespace Bazaar.Server.Wire.Sync;

/// <summary>
/// Settings the subscription engine and dispatcher read (doc 02, settings table). Defaults are the shipped
/// values; the settings registry (doc 03) may tighten them and never loosen them below these floors.
/// </summary>
public sealed class SyncOptions
{
    /// <summary><c>wire.replay_cutoff_entries</c>: a gap above this many entries plus versions gets <c>resync</c>.</summary>
    public int ReplayCutoffEntries { get; init; } = 2000;

    /// <summary><c>wire.replay_page_size</c>: entries per replay page.</summary>
    public int ReplayPageSize { get; init; } = 200;

    /// <summary><c>wire.outbound_queue_max</c>: queued events per connection before <c>lagged</c>.</summary>
    public int OutboundQueueMax { get; init; } = 1000;

    /// <summary>Entries in the <c>tail</c> of a <c>resync</c> event (doc 02: the last 100).</summary>
    public int ResyncTailSize { get; init; } = 100;

    /// <summary>Live events a subscription buffers while it replays; beyond this it re-reads from the log instead.</summary>
    public int LiveBufferMax { get; init; } = 1000;

    /// <summary>Delay before the dispatcher retries a failed log read.</summary>
    public TimeSpan PumpRetryDelay { get; init; } = TimeSpan.FromMilliseconds(250);

    /// <summary>How often the dispatcher re-reads every active GC, bounding the delay a lost notification can cause.</summary>
    public TimeSpan? SweepInterval { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>The protocol major stamped on events the engine sends.</summary>
    public int ProtocolMajor { get; init; } = 1;
}
