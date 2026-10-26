using Bazaar.Contracts.Gateway;

namespace Bazaar.Server.Gateway.Pipeline;

/// <summary>Receives the quota facts the gateway reports. The gateway reports facts only.</summary>
public interface IGatewayEventSink
{
    void Publish(GatewayQuotaEvent e);
}

/// <summary>Drops every event. The default sink.</summary>
public sealed class NullGatewayEventSink : IGatewayEventSink
{
    public static NullGatewayEventSink Instance { get; } = new();

    public void Publish(GatewayQuotaEvent e)
    {
    }
}
