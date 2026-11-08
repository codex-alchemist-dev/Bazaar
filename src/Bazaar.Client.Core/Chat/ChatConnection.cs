using Bazaar.Client.Core.Wire;
using Bazaar.Contracts.Wire.Codec;

namespace Bazaar.Client.Core.Chat;

/// <summary>Builds the chat session every desktop head uses: JSON over WebSocket, with the head's cursor store.</summary>
public static class ChatConnection
{
    /// <param name="settings">Server, token and group chat.</param>
    /// <param name="cursors">Where replay cursors are kept. Heads pass a file store under their state directory.</param>
    /// <param name="deviceClass">Device class the server uses to pick a UI profile, such as <c>linux</c>.</param>
    /// <param name="clientBuild">Build label sent in <c>hello</c>.</param>
    public static GcChatSession Create(ChatConnectionSettings settings, ICursorStore cursors, string deviceClass, string clientBuild)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(cursors);
        ArgumentException.ThrowIfNullOrWhiteSpace(deviceClass);
        ArgumentException.ThrowIfNullOrWhiteSpace(clientBuild);

        var client = new WireClient(
            new WebSocketWireTransportFactory(settings.Server),
            new JsonWireCodec(),
            cursors,
            new WireClientOptions
            {
                DeviceId = $"{deviceClass}-{Environment.MachineName}",
                DeviceClass = deviceClass,
                ClientBuild = clientBuild,
                Credential = settings.Token,
            },
            TimeProvider.System);

        var timeline = new ChatTimeline(new Dictionary<Guid, ChatMember>(), settings.Name, TimeZoneInfo.Local);
        return new GcChatSession(client, settings, timeline);
    }
}
