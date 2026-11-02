using System.Text.Json;
using System.Text.Json.Serialization;

namespace Bazaar.Contracts.Engines;

/// <summary>
/// JSON-RPC 2.0 constants. Every envelope writes <c>"jsonrpc":"2.0"</c> through a read-only member, so no caller
/// can send another version.
/// </summary>
public static class JsonRpc
{
    /// <summary>The only JSON-RPC version this protocol speaks.</summary>
    public const string Version = "2.0";
}

/// <summary>A JSON-RPC 2.0 request from the server to an adapter. A request with an <c>Id</c> expects a response.</summary>
public sealed record JsonRpcRequest(
    string? Id,
    string Method,
    JsonElement? Params = null)
{
    /// <summary>Always <see cref="JsonRpc.Version"/>. Written on the wire, never read.</summary>
    [JsonPropertyName("jsonrpc")]
    public string Jsonrpc { get; } = JsonRpc.Version;
}

/// <summary>
/// A JSON-RPC 2.0 response. Exactly one of <c>Result</c> or <c>Error</c> is set; the encoder omits the other
/// (see <see cref="EngineJson"/>).
/// </summary>
public sealed record JsonRpcResponse(
    string? Id,
    JsonElement? Result = null,
    JsonRpcError? Error = null)
{
    /// <summary>Always <see cref="JsonRpc.Version"/>. Written on the wire, never read.</summary>
    [JsonPropertyName("jsonrpc")]
    public string Jsonrpc { get; } = JsonRpc.Version;
}

/// <summary>A JSON-RPC 2.0 error object. Codes are either reserved by JSON-RPC or Bazaar-defined (<see cref="EngineErrorCodes"/>).</summary>
public sealed record JsonRpcError(
    int Code,
    string Message,
    JsonElement? Data = null);

/// <summary>A JSON-RPC 2.0 notification: a request without an id, so no response is expected. Used for streamed <c>output</c>.</summary>
public sealed record JsonRpcNotification(
    string Method,
    JsonElement? Params = null)
{
    /// <summary>Always <see cref="JsonRpc.Version"/>. Written on the wire, never read.</summary>
    [JsonPropertyName("jsonrpc")]
    public string Jsonrpc { get; } = JsonRpc.Version;
}
