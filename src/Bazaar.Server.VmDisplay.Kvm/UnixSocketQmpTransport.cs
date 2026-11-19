using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using Bazaar.Server.VmDisplay.Abstractions;

namespace Bazaar.Server.VmDisplay.Kvm;

/// <summary>
/// Speaks QMP directly over a unix socket (QEMU <c>-qmp unix:path,server</c>). It opens one connection per command: read the
/// greeting, send <c>qmp_capabilities</c>, send the command, then read lines until one carries <c>return</c> or <c>error</c>,
/// skipping asynchronous <c>event</c> lines.
/// </summary>
public sealed class UnixSocketQmpTransport : IQmpTransport
{
    private const int MaxLineChars = 1024 * 1024;
    private readonly Func<string, string> _socketPath;
    private readonly TimeSpan _timeout;

    /// <summary>Creates the transport.</summary>
    /// <param name="socketPath">Maps a validated domain name to its QMP socket path.</param>
    /// <param name="timeout">Timeout for one whole exchange.</param>
    public UnixSocketQmpTransport(Func<string, string> socketPath, TimeSpan timeout)
    {
        ArgumentNullException.ThrowIfNull(socketPath);
        _socketPath = socketPath;
        _timeout = timeout;
    }

    /// <inheritdoc />
    public async Task<string> ExecuteAsync(string domain, string qmpJson, CancellationToken ct)
    {
        if (!DomainName.IsValid(domain))
            throw new DisplayException(DisplayFailure.InvalidInput, "The VM name is not valid.");

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(_timeout);
        try
        {
            using var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
            await socket.ConnectAsync(new UnixDomainSocketEndPoint(_socketPath(domain)), cts.Token).ConfigureAwait(false);
            await using var stream = new NetworkStream(socket, ownsSocket: false);
            using var reader = new StreamReader(stream, Encoding.UTF8, false, 4096, leaveOpen: true);

            await ReadLineAsync(reader, cts.Token).ConfigureAwait(false); // greeting
            await WriteLineAsync(stream, "{\"execute\":\"qmp_capabilities\"}", cts.Token).ConfigureAwait(false);
            await ReadResponseAsync(reader, cts.Token).ConfigureAwait(false);
            await WriteLineAsync(stream, qmpJson, cts.Token).ConfigureAwait(false);
            return await ReadResponseAsync(reader, cts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new DisplayException(DisplayFailure.SourceFailed, "The QMP exchange timed out.");
        }
        catch (Exception e) when (e is IOException or SocketException or JsonException)
        {
            throw new DisplayException(DisplayFailure.SourceFailed, "The QMP socket failed.", e);
        }
    }

    private static async Task WriteLineAsync(Stream stream, string json, CancellationToken ct)
    {
        await stream.WriteAsync(Encoding.UTF8.GetBytes(json + "\n"), ct).ConfigureAwait(false);
        await stream.FlushAsync(ct).ConfigureAwait(false);
    }

    private static async Task<string> ReadLineAsync(StreamReader reader, CancellationToken ct)
    {
        string? line = await reader.ReadLineAsync(ct).ConfigureAwait(false);
        if (line is null)
            throw new DisplayException(DisplayFailure.SourceFailed, "The QMP socket closed.");
        if (line.Length > MaxLineChars)
            throw new DisplayException(DisplayFailure.SourceFailed, "A QMP line is larger than the limit.");
        return line;
    }

    private static async Task<string> ReadResponseAsync(StreamReader reader, CancellationToken ct)
    {
        while (true)
        {
            string line = await ReadLineAsync(reader, ct).ConfigureAwait(false);
            if (line.Length == 0)
                continue;
            using JsonDocument doc = JsonDocument.Parse(line);
            if (doc.RootElement.ValueKind == JsonValueKind.Object
                && (doc.RootElement.TryGetProperty("return", out _) || doc.RootElement.TryGetProperty("error", out _)))
                return line;
        }
    }
}
