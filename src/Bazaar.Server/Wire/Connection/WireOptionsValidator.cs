using Microsoft.Extensions.Options;

namespace Bazaar.Server.Wire.Connection;

/// <summary>Rejects wire settings that loosen a floor in doc 02, or that make no sense.</summary>
public sealed class WireOptionsValidator : IValidateOptions<WireOptions>
{
    private static readonly WireOptions _floors = new();

    /// <inheritdoc />
    public ValidateOptionsResult Validate(string? name, WireOptions o)
    {
        var errors = new List<string>();
        void AtMost(string key, int value, int floor)
        {
            if (value < 1 || value > floor) errors.Add($"{key} must be between 1 and {floor} (settings may tighten, never loosen).");
        }

        AtMost("wire.hello_timeout_s", o.HelloTimeoutS, _floors.HelloTimeoutS);
        AtMost("wire.heartbeat_timeout_s", o.HeartbeatTimeoutS, _floors.HeartbeatTimeoutS);
        AtMost("wire.max_sockets_per_device", o.MaxSocketsPerDevice, _floors.MaxSocketsPerDevice);
        AtMost("wire.outbound_queue_max", o.OutboundQueueMax, _floors.OutboundQueueMax);
        AtMost("wire.inflight_requests_max", o.InflightRequestsMax, _floors.InflightRequestsMax);
        AtMost("wire.max_frame_kib", o.MaxFrameKib, _floors.MaxFrameKib);
        AtMost("wire.heartbeat_interval_s", o.HeartbeatIntervalS, _floors.HeartbeatIntervalS);
        if (o.HeartbeatIntervalS >= o.HeartbeatTimeoutS) errors.Add("wire.heartbeat_interval_s must be below wire.heartbeat_timeout_s.");
        if (o.RequestIdRetentionH < _floors.RequestIdRetentionH) errors.Add("wire.request_id_retention_h cannot go below 24.");
        if (o.RequestsPerSecond < 1 || o.RequestBurst < 1 || o.ReplayPageSize < 1) errors.Add("Rate and page settings must be positive.");
        if (string.IsNullOrEmpty(o.Path) || o.Path[0] != '/') errors.Add("Path must start with '/'.");
        return errors.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(errors);
    }
}
