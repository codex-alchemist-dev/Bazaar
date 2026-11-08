using System.Threading.Channels;
using Bazaar.Contracts.Wire;

namespace Bazaar.Client.Components.Chat;

/// <summary>
/// The wire operations the chat component needs, as a contract. The Windows head adapts the shared wire client to this,
/// so the component never references the client core.
/// </summary>
public interface IGcWire
{
    /// <summary>Gap-free <c>gc.message</c> events for the streams the client subscribed to.</summary>
    ChannelReader<WireGcMessage> Messages { get; }

    /// <summary>Subscribes to a stream such as <c>gc:&lt;id&gt;</c>.</summary>
    Task SubscribeAsync(string stream, CancellationToken cancellationToken);

    /// <summary>Request <c>gc.send</c>.</summary>
    Task<GcSendResult> SendAsync(GcSend request, CancellationToken cancellationToken);

    /// <summary>Request <c>gc.history</c>: older entries, oldest first.</summary>
    Task<GcPage> HistoryAsync(GcHistory request, CancellationToken cancellationToken);
}
