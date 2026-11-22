using Bazaar.Server.Gateway.Adapters;

namespace Bazaar.Server.Gateway.Health;

/// <summary>
/// The outcome of one active probe. <c>Detail</c> is a short fixed-form reason (for example <c>HTTP 500</c> or
/// <c>timeout</c>). It never carries a key, a header value or an upstream body.
/// </summary>
public sealed record ProbeResult(bool Healthy, TimeSpan Latency, string Detail);

/// <summary>
/// An active health check for one provider endpoint. Implementations must use a cheap call (a model list, never a
/// paid completion). They report upstream problems as an unhealthy <see cref="ProbeResult"/>; only the caller's own
/// cancellation may surface as an exception.
/// </summary>
public interface IHealthProbe
{
    Task<ProbeResult> ProbeAsync(ProviderEndpoint endpoint, CancellationToken ct);
}
