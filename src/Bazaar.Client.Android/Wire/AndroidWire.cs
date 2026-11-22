using Bazaar.Client.Abstractions.Server;
using Bazaar.Client.Abstractions.Wire;

namespace Bazaar.Client.Android.Wire;

/// <summary>
/// The phone's one socket to its company server (doc 29, Transport). Built from <see cref="ClientServer.Current"/> after the head
/// has connected; panes that send wire requests (Kanban, approvals) share it as <see cref="IWireRequester"/>. It runs until the
/// process ends: reconnects and request retries are the client's own (doc 02). The device id and the replay cursors live in
/// the app's private files.
/// </summary>
public sealed class AndroidWire : IDisposable
{
    private readonly CancellationTokenSource _stop = new();

    private AndroidWire(WireClient client) => Client = client;

    /// <summary>The socket client. Requests wait for a connection and are retried across reconnects.</summary>
    public WireClient Client { get; }

    /// <summary>Opens the client for <paramref name="endpoint"/> and starts its connection loop.</summary>
    /// <param name="endpoint">The company server, normally <see cref="ClientServer.Current"/>.</param>
    /// <param name="stateDirectory">The app's private directory for the device id and cursors.</param>
    /// <param name="clientBuild">Build label sent in <c>hello</c>.</param>
    public static AndroidWire Start(ServerEndpoint endpoint, string stateDirectory, string clientBuild)
    {
        ArgumentNullException.ThrowIfNull(endpoint);
        ArgumentException.ThrowIfNullOrWhiteSpace(stateDirectory);
        ArgumentException.ThrowIfNullOrWhiteSpace(clientBuild);

        Directory.CreateDirectory(stateDirectory);
        var client = EndpointWireClient.Create(
            endpoint,
            new FileCursorStore(Path.Combine(stateDirectory, "cursors.json")),
            DeviceId(Path.Combine(stateDirectory, "device-id")),
            "android",
            clientBuild);
        var wire = new AndroidWire(client);
        _ = Task.Run(() => client.RunAsync(wire._stop.Token));
        return wire;
    }

    /// <summary>A random id made on first run and kept, so the server sees one device across restarts. It identifies no person.</summary>
    internal static string DeviceId(string path)
    {
        if (File.Exists(path) && File.ReadAllText(path).Trim() is { Length: > 0 } existing)
        {
            return existing;
        }

        var id = $"android-{Guid.NewGuid():N}";
        File.WriteAllText(path, id);
        return id;
    }

    public void Dispose()
    {
        _stop.Cancel();
        _stop.Dispose();
    }
}
