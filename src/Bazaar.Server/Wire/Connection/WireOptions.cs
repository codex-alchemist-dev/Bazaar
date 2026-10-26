namespace Bazaar.Server.Wire.Connection;

/// <summary>
/// Wire limits (doc 02 settings table, <c>wire.*</c>). Defaults are the floors: a setting may tighten them and
/// <see cref="WireOptionsValidator"/> refuses a value that loosens them.
/// </summary>
public sealed class WireOptions
{
    /// <summary>Configuration section name.</summary>
    public const string SectionName = "Bazaar:Wire";

    /// <summary>URL path of the socket endpoint.</summary>
    public string Path { get; set; } = "/ws";

    /// <summary><c>wire.hello_timeout_s</c>.</summary>
    public int HelloTimeoutS { get; set; } = 5;

    /// <summary><c>wire.heartbeat_interval_s</c>.</summary>
    public int HeartbeatIntervalS { get; set; } = 20;

    /// <summary><c>wire.heartbeat_timeout_s</c>.</summary>
    public int HeartbeatTimeoutS { get; set; } = 60;

    /// <summary><c>wire.max_sockets_per_device</c>; the newest socket wins.</summary>
    public int MaxSocketsPerDevice { get; set; } = 5;

    /// <summary><c>wire.outbound_queue_max</c>, in events.</summary>
    public int OutboundQueueMax { get; set; } = 1000;

    /// <summary><c>wire.inflight_requests_max</c>.</summary>
    public int InflightRequestsMax { get; set; } = 32;

    /// <summary><c>wire.max_frame_kib</c>.</summary>
    public int MaxFrameKib { get; set; } = 256;

    /// <summary><c>wire.request_id_retention_h</c>.</summary>
    public int RequestIdRetentionH { get; set; } = 24;

    /// <summary><c>wire.replay_page_size</c>; announced in <c>welcome</c>.</summary>
    public int ReplayPageSize { get; set; } = 200;

    /// <summary><c>wire.previous_major_supported</c>.</summary>
    public bool PreviousMajorSupported { get; set; } = true;

    /// <summary>Requests per second one connection may sustain.</summary>
    public int RequestsPerSecond { get; set; } = 50;

    /// <summary>Requests one connection may burst above the sustained rate.</summary>
    public int RequestBurst { get; set; } = 100;

    /// <summary>Consecutive rate-limited requests after which the socket closes with 4429.</summary>
    public int RateLimitCloseAfter { get; set; } = 100;

    /// <summary>Lowest client build named in <c>welcome</c> when no version overlaps.</summary>
    public string MinimumBuild { get; set; } = "0.1.0";

    /// <summary>Capability flags announced in <c>welcome</c>.</summary>
    public IList<string> Capabilities { get; set; } = [];

    /// <summary><c>wire.allow_plaintext_overlay</c>: accept non-TLS sockets from non-loopback peers. Owner approval needed to set.</summary>
    public bool AllowPlaintextOverlay { get; set; }

    internal int MaxFrameBytes => MaxFrameKib * 1024;
}
