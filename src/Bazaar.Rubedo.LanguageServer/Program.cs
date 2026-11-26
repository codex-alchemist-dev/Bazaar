using System.Net;
using System.Net.Sockets;
using Bazaar.Rubedo.LanguageServer;

// Stdio by default. `--socket` listens on 127.0.0.1 only, on a random port printed to stderr, and serves one client at a time.
if (!ServerOptions.TryParse(args, out ServerOptions options, out string? problem))
{
    await Console.Error.WriteLineAsync(problem).ConfigureAwait(false);
    return 2;
}

bool socket = options.Socket;
using var cts = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) =>
{
    e.Cancel = true;
    cts.Cancel();
};

if (!socket)
{
    using Stream stdin = Console.OpenStandardInput();
    using Stream stdout = Console.OpenStandardOutput();
    return await LanguageServerHost.RunAsync(stdin, stdout, options.Schema, cts.Token).ConfigureAwait(false);
    return await LanguageServerHost.RunAsync(stdin, stdout, options.Schema, options.Tokens, cts.Token).ConfigureAwait(false);
}

var listener = new TcpListener(IPAddress.Loopback, 0);
listener.Start();
int port = ((IPEndPoint)listener.LocalEndpoint).Port;
await Console.Error.WriteLineAsync($"Rubedo language server listening on 127.0.0.1:{port}").ConfigureAwait(false);
try
{
    using TcpClient client = await listener.AcceptTcpClientAsync(cts.Token).ConfigureAwait(false);
    using NetworkStream stream = client.GetStream();
    return await LanguageServerHost.RunAsync(stream, stream, options.Schema, cts.Token).ConfigureAwait(false);
    return await LanguageServerHost.RunAsync(stream, stream, options.Schema, options.Tokens, cts.Token).ConfigureAwait(false);
}
catch (OperationCanceledException)
{
    return 1;
}
finally
{
    listener.Stop();
}
