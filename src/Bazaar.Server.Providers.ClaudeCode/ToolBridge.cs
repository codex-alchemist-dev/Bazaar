using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Bazaar.Contracts.Gateway;

namespace Bazaar.Server.Providers.ClaudeCode;

/// <summary>
/// The definition-only MCP bridge of docs/design/91 section 7.2. It lists the request's tools so the model can call them, and it
/// never runs one: <c>tools/call</c> always answers with an error. Loopback only, one random bearer token per bridge.
/// </summary>
internal sealed class ToolBridge : IAsyncDisposable
{
    private const int _maxBodyBytes = 1024 * 1024;
    private readonly HttpListener _listener;
    private readonly IReadOnlyList<ModelToolDefinition> _tools;
    private readonly byte[] _tokenBytes;
    private readonly CancellationTokenSource _stop = new();
    private Task _loop = Task.CompletedTask;
    private int _callAttempts;

    private ToolBridge(HttpListener listener, string url, string token, IReadOnlyList<ModelToolDefinition> tools)
    {
        _listener = listener;
        Url = url;
        Token = token;
        _tokenBytes = Encoding.UTF8.GetBytes("Bearer " + token);
        _tools = tools;
    }

    public string Url { get; }

    public string Token { get; }

    /// <summary>How many times the model reached the bridge's <c>tools/call</c> (always refused).</summary>
    public int CallAttempts => Volatile.Read(ref _callAttempts);

    public static ToolBridge Start(IReadOnlyList<ModelToolDefinition> tools)
    {
        ArgumentNullException.ThrowIfNull(tools);
        var token = Convert.ToHexString(RandomNumberGenerator.GetBytes(24)).ToLowerInvariant();
        for (var attempt = 0; attempt < 8; attempt++)
        {
            var probe = new TcpListener(IPAddress.Loopback, 0);
            probe.Start();
            var port = ((IPEndPoint)probe.LocalEndpoint).Port;
            probe.Stop();
            var url = $"http://127.0.0.1:{port}/mcp/";
            var listener = new HttpListener();
            listener.Prefixes.Add(url);
            try
            {
                listener.Start();
            }
            catch (HttpListenerException)
            {
                listener.Close();
                continue;
            }

            var bridge = new ToolBridge(listener, url, token, tools);
            bridge._loop = Task.Run(bridge.RunAsync);
            return bridge;
        }

        throw new InvalidOperationException("No loopback port was available for the tool bridge.");
    }

    /// <summary>The <c>--mcp-config</c> document. It holds the bearer token, so it goes in a private file, never on the command line.</summary>
    public string BuildMcpConfigJson()
    {
        using var ms = new MemoryStream();
        using (var w = new Utf8JsonWriter(ms))
        {
            w.WriteStartObject();
            w.WriteStartObject("mcpServers");
            w.WriteStartObject(ClaudeCodeArguments.BridgeServerName);
            w.WriteString("type", "http");
            w.WriteString("url", Url);
            w.WriteStartObject("headers");
            w.WriteString("Authorization", "Bearer " + Token);
            w.WriteEndObject();
            w.WriteEndObject();
            w.WriteEndObject();
            w.WriteEndObject();
        }

        return Encoding.UTF8.GetString(ms.ToArray());
    }

    public async ValueTask DisposeAsync()
    {
        await _stop.CancelAsync().ConfigureAwait(false);
        try
        {
            _listener.Close();
        }
        catch (ObjectDisposedException)
        {
        }

        try
        {
            await _loop.ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is HttpListenerException or ObjectDisposedException or OperationCanceledException)
        {
        }

        _stop.Dispose();
    }

    private async Task RunAsync()
    {
        while (!_stop.IsCancellationRequested)
        {
            HttpListenerContext ctx;
            try
            {
                ctx = await _listener.GetContextAsync().ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is HttpListenerException or ObjectDisposedException or InvalidOperationException)
            {
                return;
            }

            try
            {
                await HandleAsync(ctx).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is HttpListenerException or IOException or ObjectDisposedException or JsonException)
            {
                // A broken request never takes the bridge down.
            }
        }
    }

    private async Task HandleAsync(HttpListenerContext ctx)
    {
        var res = ctx.Response;
        var auth = Encoding.UTF8.GetBytes(ctx.Request.Headers["Authorization"] ?? "");
        if (!CryptographicOperations.FixedTimeEquals(auth, _tokenBytes))
        {
            res.StatusCode = 401;
            res.Close();
            return;
        }

        if (ctx.Request.HttpMethod != "POST" || ctx.Request.Url?.AbsolutePath.TrimEnd('/') != "/mcp")
        {
            res.StatusCode = 405;
            res.Close();
            return;
        }

        if (ctx.Request.ContentLength64 > _maxBodyBytes)
        {
            res.StatusCode = 413;
            res.Close();
            return;
        }

        var buffer = new byte[Math.Min(Math.Max(ctx.Request.ContentLength64, 0), _maxBodyBytes)];
        var read = 0;
        while (read < buffer.Length)
        {
            var n = await ctx.Request.InputStream.ReadAsync(buffer.AsMemory(read), _stop.Token).ConfigureAwait(false);
            if (n == 0)
            {
                break;
            }

            read += n;
        }

        using var doc = JsonDocument.Parse(buffer.AsMemory(0, read));
        var root = doc.RootElement;
        if (!root.TryGetProperty("id", out var id) || root.TryGetProperty("method", out var m0) && m0.GetString()?.StartsWith("notifications/", StringComparison.Ordinal) == true)
        {
            res.StatusCode = 202;
            res.Close();
            return;
        }

        var method = root.TryGetProperty("method", out var m) ? m.GetString() : null;
        using var ms = new MemoryStream();
        using (var w = new Utf8JsonWriter(ms))
        {
            w.WriteStartObject();
            w.WriteString("jsonrpc", "2.0");
            w.WritePropertyName("id");
            id.WriteTo(w);
            switch (method)
            {
                case "initialize":
                    var version = root.TryGetProperty("params", out var p) && p.TryGetProperty("protocolVersion", out var pv) ? pv.GetString() : null;
                    w.WriteStartObject("result");
                    w.WriteString("protocolVersion", version ?? "2025-06-18");
                    w.WriteStartObject("capabilities");
                    w.WriteStartObject("tools");
                    w.WriteEndObject();
                    w.WriteEndObject();
                    w.WriteStartObject("serverInfo");
                    w.WriteString("name", ClaudeCodeArguments.BridgeServerName);
                    w.WriteString("version", "0.1.0");
                    w.WriteEndObject();
                    w.WriteEndObject();
                    break;
                case "ping":
                    w.WriteStartObject("result");
                    w.WriteEndObject();
                    break;
                case "tools/list":
                    w.WriteStartObject("result");
                    w.WriteStartArray("tools");
                    foreach (var t in _tools)
                    {
                        w.WriteStartObject();
                        w.WriteString("name", t.Name);
                        w.WriteString("description", t.Description);
                        w.WritePropertyName("inputSchema");
                        WriteSchema(w, t.ParametersJsonSchema);
                        w.WriteEndObject();
                    }

                    w.WriteEndArray();
                    w.WriteEndObject();
                    break;
                case "tools/call":
                    Interlocked.Increment(ref _callAttempts);
                    w.WriteStartObject("result");
                    w.WriteBoolean("isError", true);
                    w.WriteStartArray("content");
                    w.WriteStartObject();
                    w.WriteString("type", "text");
                    w.WriteString("text", "Tool calls are carried out by the host after this step. Stop and wait.");
                    w.WriteEndObject();
                    w.WriteEndArray();
                    w.WriteEndObject();
                    break;
                default:
                    w.WriteStartObject("error");
                    w.WriteNumber("code", -32601);
                    w.WriteString("message", "method not found");
                    w.WriteEndObject();
                    break;
            }

            w.WriteEndObject();
        }

        res.StatusCode = 200;
        res.ContentType = "application/json";
        res.ContentLength64 = ms.Length;
        await res.OutputStream.WriteAsync(ms.ToArray(), _stop.Token).ConfigureAwait(false);
        res.Close();
    }

    private static void WriteSchema(Utf8JsonWriter w, string schema)
    {
        try
        {
            using var d = JsonDocument.Parse(schema);
            if (d.RootElement.ValueKind == JsonValueKind.Object)
            {
                d.RootElement.WriteTo(w);
                return;
            }
        }
        catch (JsonException)
        {
        }

        w.WriteStartObject();
        w.WriteString("type", "object");
        w.WriteEndObject();
    }
}
