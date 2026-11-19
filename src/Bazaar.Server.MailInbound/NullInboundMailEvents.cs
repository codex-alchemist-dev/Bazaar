namespace Bazaar.Server.MailInbound;

/// <summary>The default <see cref="IInboundMailEvents"/>: reports nothing.</summary>
public sealed class NullInboundMailEvents : IInboundMailEvents
{
    public static NullInboundMailEvents Instance { get; } = new();

    public void Report(InboundEvent inboundEvent)
    {
    }
}
