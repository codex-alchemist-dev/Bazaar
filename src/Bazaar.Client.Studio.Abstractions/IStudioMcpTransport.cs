using System.Text.Json;

namespace Bazaar.Client.Studio.Abstractions;

/// <summary>The link to Studio's built-in MCP server (JSON-RPC 2.0 over stdio). Studio has no authentication, so only the bridge process connects.</summary>
public interface IStudioMcpTransport : IAsyncDisposable
{
    /// <summary>True while the MCP server is running and answering, which is the bridge's signal that Studio is reachable.</summary>
    bool IsStudioOpen { get; }

    /// <summary>Raised when <see cref="IsStudioOpen"/> changes.</summary>
    event EventHandler? IsStudioOpenChanged;

    /// <summary>Starts the server process and performs the MCP handshake. Does nothing when already running.</summary>
    Task StartAsync(CancellationToken cancellationToken = default);

    /// <summary>Stops the server process and fails pending calls.</summary>
    Task StopAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Sends one JSON-RPC request line and awaits the response with the matching id, returning its <c>result</c>.
    /// Throws <see cref="StudioTransportException"/> on a JSON-RPC error response, an exited process or a write failure.
    /// </summary>
    Task<JsonElement> CallAsync(string method, JsonElement? parameters, CancellationToken cancellationToken = default);
}

/// <summary>The transport failed: the process exited, the pipe broke, or the server answered with a JSON-RPC error.</summary>
public sealed class StudioTransportException : Exception
{
    /// <summary>Creates the exception.</summary>
    public StudioTransportException(string message, int? rpcCode = null, Exception? inner = null)
        : base(message, inner)
    {
        RpcCode = rpcCode;
    }

    /// <summary>The JSON-RPC error code when the server answered with an error; null for process or pipe failures.</summary>
    public int? RpcCode { get; }
}
