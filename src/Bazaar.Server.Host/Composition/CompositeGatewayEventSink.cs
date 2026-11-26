using Bazaar.Contracts.Gateway;
using Bazaar.Server.Gateway.Pipeline;
using Bazaar.Server.Presence.Sources;
using Microsoft.Extensions.Logging;

namespace Bazaar.Server.Host.Composition;

/// <summary>
/// Hands each gateway quota fact to every sink the host has: any <see cref="IGatewayEventSink"/> an operator registered, and
/// the presence module's quota source when it is loaded (docs/design/23, "Event sources"). The gateway reports facts only and stays
/// unaware of presence. A sink that throws never reaches the gateway and never stops the next sink; the error goes to
/// <paramref name="onSinkFailure"/>.
/// </summary>
internal sealed class CompositeGatewayEventSink(IReadOnlyList<IGatewayEventSink> sinks, Action<Exception>? onSinkFailure = null) : IGatewayEventSink
{
    /// <summary>Returns null when there is nothing to forward to, so the gateway keeps its own default sink.</summary>
    public static IGatewayEventSink? Compose(IEnumerable<IGatewayEventSink?> sinks, Action<Exception>? onSinkFailure = null)
    {
        var present = sinks.OfType<IGatewayEventSink>().ToArray();
        return present.Length == 0 ? null : new CompositeGatewayEventSink(present, onSinkFailure);
    }

    public void Publish(GatewayQuotaEvent e)
    {
        foreach (var sink in sinks)
        {
            try
            {
                sink.Publish(e);
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                onSinkFailure?.Invoke(ex);
            }
        }
    }
}

/// <summary>Feeds the gateway's quota facts to presence. <see cref="QuotaPresenceSource.Publish"/> already drops its own failures.</summary>
internal sealed class QuotaPresenceGatewayEventSink(QuotaPresenceSource source) : IGatewayEventSink
{
    public void Publish(GatewayQuotaEvent e) => source.Publish(e);
}

internal static partial class GatewayEventSinkLog
{
    [LoggerMessage(Level = LogLevel.Debug, Message = "A gateway quota event sink failed; the gateway carries on.")]
    public static partial void SinkFailed(ILogger logger, Exception exception);
}
