namespace Bazaar.Server.Gateway.Ai;

/// <summary>
/// A failure the gateway reported in the model event stream (<see cref="Bazaar.Contracts.Gateway.ModelEvent.Failure"/>),
/// or a stream the adapter could not translate, such as tool-call argument JSON that is not valid.
/// <see cref="Code"/> is the stable machine-readable name (for example <c>rate_limited</c> or <c>malformed_tool_json</c>).
/// </summary>
public sealed class GatewayModelException(string code, string message, int? retryAfterSeconds = null) : Exception(message)
{
    /// <summary>The stable failure name from the gateway.</summary>
    public string Code { get; } = code;

    /// <summary>Seconds the caller should wait before retrying, when the upstream said so.</summary>
    public int? RetryAfterSeconds { get; } = retryAfterSeconds;
}
