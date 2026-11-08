using System.IO.Pipes;
using System.Text.Json;

namespace Bazaar.Client.Components.Launcher;

/// <summary>
/// Single-instance hand-off. A second launch forwards its <see cref="LaunchRequest"/> over a per-user
/// named pipe to the running instance, which answers <c>ok</c>. One JSON line each way.
/// </summary>
public static class LaunchPipe
{
    /// <summary>Per-user pipe name, so two Windows accounts on one machine never collide.</summary>
    public static string DefaultName(string userName) => $"bazaar-launcher-{userName}";

    /// <summary>
    /// Sends the request to a running instance. Returns false when nothing answers within <paramref name="timeout"/>,
    /// which means the caller should start as the primary instance.
    /// </summary>
    public static async Task<bool> TryForwardAsync(string pipeName, LaunchRequest request, TimeSpan timeout, CancellationToken ct)
    {
        using var client = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
        try
        {
            await client.ConnectAsync((int)timeout.TotalMilliseconds, ct);
        }
        catch (TimeoutException)
        {
            return false;
        }

        using var writer = new StreamWriter(client, leaveOpen: true) { AutoFlush = true };
        using var reader = new StreamReader(client, leaveOpen: true);
        var json = JsonSerializer.Serialize(request, LaunchJsonContext.Default.LaunchRequest);
        await writer.WriteLineAsync(json.AsMemory(), ct);
        var reply = await reader.ReadLineAsync(ct);
        return reply == "ok";
    }

    /// <summary>
    /// Accepts forwarded requests until <paramref name="ct"/> is cancelled. Each request is handed to
    /// <paramref name="onRequest"/> and then acknowledged.
    /// </summary>
    public static async Task ListenAsync(string pipeName, Func<LaunchRequest, Task> onRequest, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            await using var server = new NamedPipeServerStream(
                pipeName,
                PipeDirection.InOut,
                1,
                PipeTransmissionMode.Byte,
                PipeOptions.Asynchronous);

            await server.WaitForConnectionAsync(ct);

            using var reader = new StreamReader(server, leaveOpen: true);
            using var writer = new StreamWriter(server, leaveOpen: true) { AutoFlush = true };
            var line = await reader.ReadLineAsync(ct);
            if (line is null)
            {
                continue;
            }

            var request = JsonSerializer.Deserialize(line, LaunchJsonContext.Default.LaunchRequest) ?? LaunchRequest.Empty;
            await onRequest(request);
            await writer.WriteLineAsync("ok");
        }
    }
}
