using System.Threading.Channels;
using Bazaar.Client.Components.Chat;
using Bazaar.Client.Core.Wire;
using Bazaar.Contracts.Wire;

namespace Bazaar.Client.Windows.Chat;

/// <summary>
/// Adapts the shared wire client to the chat component's <see cref="IGcWire"/> contract. Host composition builds it;
/// nothing else references the wire client from the chat component.
/// </summary>
internal sealed class WireGcAdapter(WireClient wire) : IGcWire
{
    public ChannelReader<WireGcMessage> Messages => wire.Messages;

    public Task SubscribeAsync(string stream, CancellationToken cancellationToken) =>
        wire.SubscribeAsync(stream, cancellationToken);

    public Task<GcSendResult> SendAsync(GcSend request, CancellationToken cancellationToken) =>
        wire.RequestAsync<GcSend, GcSendResult>(WireTypes.GcSend, request, cancellationToken);

    public Task<GcPage> HistoryAsync(GcHistory request, CancellationToken cancellationToken) =>
        wire.RequestAsync<GcHistory, GcPage>(WireTypes.GcHistory, request, cancellationToken);
}
