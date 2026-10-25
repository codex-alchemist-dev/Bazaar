using Microsoft.Extensions.Options;

namespace Bazaar.Server.Wire.Connection;

/// <summary>Rejects wire settings that loosen a floor in doc 02, or that make no sense.</summary>
public sealed class WireOptionsValidator : IValidateOptions<WireOptions>
{
    private static readonly WireOptions Floors = new();

    /// <inheritdoc />
    public ValidateOptionsResult Validate(string? name, WireOptions o)
    {
        var errors = new List<string>();
        void AtMost(string key, int value, int floor)
        {
            if (value < 1 || value > floor) errors.Add($"{key} must be between 1 and {floor} (settings may tighten, never loosen).");
        }

        AtMost("wire.hello_timeout_s", o.HelloTimeoutS, Floors.HelloTimeoutS);
        AtMost("wire.heartbeat_timeout_s", o.HeartbeatTimeoutS, Floors.HeartbeatTimeoutS);
        AtMost("wire.max_sockets_per_device", o.MaxSocketsPerDevice, Floors.MaxSocketsPerDevice);
        AtMost("wire.outbound_queue_max", o.OutboundQueueMax, Floors.OutboundQueueMax);
        AtMost("wire.inflight_requests_max", o.InflightRequestsMax, Floors.InflightRequestsMax);
        AtMost("wire.max_frame_kib", o.MaxFrameKib, Floors.MaxFrameKib);
        AtMost("wire.heartbeat_interval_s", o.HeartbeatIntervalS, Floors.HeartbeatIntervalS);
        if (o.HeartbeatIntervalS >= o.HeartbeatTimeoutS) errors.Add("wire.heartbeat_interval_s must be below wire.heartbeat_timeout_s.");
        if (o.RequestIdRetentionH < Floors.RequestIdRetentionH) errors.Add("wire.request_id_retention_h cannot go below 24.");
        if (o.RequestsPerSecond < 1 || o.RequestBurst < 1 || o.ReplayPageSize < 1) errors.Add("Rate and page settings must be positive.");
        if (string.IsNullOrEmpty(o.Path) || o.Path[0] != '/') errors.Add("Path must start with '/'.");
        return errors.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(errors);
    }
}
