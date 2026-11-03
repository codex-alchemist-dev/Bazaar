using Bazaar.Contracts.Gateway;

namespace Bazaar.Server.Gateway.Pipeline;

/// <summary>Receives the quota facts the gateway reports. The gateway reports facts only.</summary>
// TODO(component): the real sink, which hands AgentCapacityChanged to the Work-Life Simulation (doc 09), waits for the
// component model (PR #125, not merged). Until then the host uses NullGatewayEventSink.
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
