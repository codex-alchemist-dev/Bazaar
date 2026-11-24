using Bazaar.Client.Abstractions.Server;
using Bazaar.Client.Abstractions.Wire;
using Bazaar.Client.Studio;
using Bazaar.Client.Studio.Abstractions;
using Bazaar.Contracts.Wire.Codec;

namespace Bazaar.Client.Windows;

/// <summary>
/// Composes the Studio bridge into the Windows head [PROPOSED] (doc 20 settings, doc 28 Desktop Studio bridge, doc 40 section 4b).
/// The bridge starts only when <c>engine.studio.mcp_path</c> (mirrored as <see cref="PathVariable"/> until the settings registry
/// reaches the client) names an existing file. <c>auto</c>, or nothing, looks for a verified install location; none is verified
/// yet, so the bridge stays off. Nothing here throws into the head: a failed start leaves the bridge off and reports why.
/// </summary>
public static class StudioComposition
{
    /// <summary>Environment mirror of the <c>engine.studio.mcp_path</c> setting.</summary>
    public const string PathVariable = "BAZAAR_STUDIO_MCP_PATH";

    /// <summary>Value of the setting that asks the head to look for the executable itself.</summary>
    public const string AutoValue = "auto";

    /// <summary>
    /// Resolves the Studio MCP executable, or null when the bridge should stay off. A set path that does not exist gives null;
    /// <c>auto</c> or an empty value asks <paramref name="autoDetect"/>, which defaults to finding nothing.
    /// </summary>
    public static string? ResolvePath(string? setting, Func<string, bool> fileExists, Func<string?>? autoDetect = null)
    {
        ArgumentNullException.ThrowIfNull(fileExists);
        var value = setting?.Trim();
        if (string.IsNullOrEmpty(value) || string.Equals(value, AutoValue, StringComparison.OrdinalIgnoreCase))
        {
            var found = autoDetect?.Invoke();
            return found is not null && fileExists(found) ? found : null;
        }

        return fileExists(value) ? value : null;
    }

    /// <summary>
    /// Starts the bridge in the background when a path resolves; returns null when it stays off. The caller disposes the result on
    /// shutdown, which stops the loop, the wire client and the Studio MCP process.
    /// </summary>
    public static IAsyncDisposable? Start(
        ServerEndpoint endpoint,
        ICursorStore cursors,
        string? pathSetting,
        Action<string> report,
        Func<string?>? autoDetect = null)
    {
        ArgumentNullException.ThrowIfNull(endpoint);
        ArgumentNullException.ThrowIfNull(cursors);
        ArgumentNullException.ThrowIfNull(report);

        var path = ResolvePath(pathSetting, File.Exists, autoDetect);
        if (path is null)
        {
            return null;
        }

        try
        {
            // A device id of its own: the chat connection keeps "bazaar-windows" and its cursors.
            var deviceId = "studio-" + Environment.MachineName.ToLowerInvariant();
            var client = EndpointWireClient.Create(endpoint, cursors, deviceId, "windows", "bazaar-windows");
            var (relay, transport) = StudioBridge.Create(new StdioMcpServerCommand(path, []), new WireStudioGrantVerifier(client));
            var loop = new StudioRelayWireClient(
                new WireRelayEventSource(client, new JsonWireCodec()),
                new WireRelayResultSender(client),
                relay,
                new StudioRelayWireOptions { OnError = e => report($"Studio bridge: {e.Message}") });

            var cts = new CancellationTokenSource();
            var wire = Task.Run(() => client.RunAsync(cts.Token), CancellationToken.None);
            var run = Task.Run(() => loop.RunAsync(cts.Token), CancellationToken.None);
            return new Running(cts, [wire, run], transport);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            report($"Studio bridge did not start: {e.Message}");
            return null;
        }
    }

    private sealed class Running(CancellationTokenSource cts, Task[] tasks, IStudioMcpTransport transport) : IAsyncDisposable
    {
        public async ValueTask DisposeAsync()
        {
            await cts.CancelAsync().ConfigureAwait(false);
            try
            {
                await Task.WhenAll(tasks).WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
            }
            catch (Exception)
            {
                // Cancellation and a slow stop are both fine at shutdown.
            }

            await transport.DisposeAsync().ConfigureAwait(false);
            cts.Dispose();
        }
    }
}
