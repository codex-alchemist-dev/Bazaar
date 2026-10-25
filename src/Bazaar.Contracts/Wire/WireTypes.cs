namespace Bazaar.Contracts.Wire;

/// <summary>Message types (<c>t</c>), the v1 sketch in docs/design/02-wire-protocol-and-sync.md.</summary>
public static class WireTypes
{
    /// <summary>Client opening message: versions, codecs, device, cursors.</summary>
    public const string Hello = "hello";

    /// <summary>Server answer to <see cref="Hello"/>.</summary>
    public const string Welcome = "welcome";

    /// <summary>Subscribe to streams with cursors.</summary>
    public const string Subscribe = "subscribe";

    /// <summary>Drop streams.</summary>
    public const string Unsubscribe = "unsubscribe";

    /// <summary>A GC message or message version, pushed by the server.</summary>
    public const string GcMessage = "gc.message";

    /// <summary>Client posts a message.</summary>
    public const string GcSend = "gc.send";

    /// <summary>Client pages older entries.</summary>
    public const string GcHistory = "gc.history";

    /// <summary>Client marks a GC read up to a sequence.</summary>
    public const string GcRead = "gc.read";

    /// <summary>Replay gap too large; the client resyncs from the tail.</summary>
    public const string Resync = "resync";

    /// <summary>The server stopped live delivery on a slow stream.</summary>
    public const string Lagged = "lagged";

    /// <summary>Access to a stream was withdrawn.</summary>
    public const string StreamRevoked = "stream.revoked";

    /// <summary>Presence delta.</summary>
    public const string PresenceUpdate = "presence.update";

    /// <summary>Client asks for its server-driven UI profile.</summary>
    public const string UiGet = "ui.get";

    /// <summary>The UI profile changed; carries a revision.</summary>
    public const string UiChanged = "ui.changed";

    /// <summary>Client sets a preference.</summary>
    public const string PrefsSet = "prefs.set";

    /// <summary>Client refreshes its credential.</summary>
    public const string AuthRefresh = "auth.refresh";

    /// <summary>Client registers a push token.</summary>
    public const string DeviceRegisterPush = "device.registerPush";

    /// <summary>Application-level keep-alive from a client.</summary>
    public const string Ping = "ping";

    /// <summary>Answer to <see cref="Ping"/>.</summary>
    public const string Pong = "pong";
}
