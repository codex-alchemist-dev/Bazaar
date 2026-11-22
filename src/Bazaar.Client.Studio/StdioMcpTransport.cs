using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Bazaar.Client.Studio.Abstractions;

namespace Bazaar.Client.Studio;

/// <summary>How to start Studio's MCP server: an executable and an argument list. Never a shell string; no MCP definitions are read from project files.</summary>
/// <param name="Command">Path of the Studio MCP server executable.</param>
/// <param name="Arguments">Arguments, passed one by one (no shell parsing).</param>
/// <param name="WorkingDirectory">Optional working directory.</param>
public sealed record StdioMcpServerCommand(string Command, IReadOnlyList<string> Arguments, string? WorkingDirectory = null);

/// <summary>
/// Speaks line-delimited JSON-RPC 2.0 to Studio's MCP server over a child process's stdio, correlating responses to requests by id.
/// When the process exits every pending call fails with <see cref="StudioTransportException"/>.
/// </summary>
public sealed class StdioMcpTransport : IStudioMcpTransport
{
    private const string _protocolVersion = "2025-06-18";
    private readonly StdioMcpServerCommand? _command;
    private readonly SemaphoreSlim _lifecycle = new(1, 1);
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private readonly Dictionary<long, TaskCompletionSource<JsonElement>> _pending = [];
    private readonly object _gate = new();
    private Process? _process;
    private TextReader? _reader;
    private TextWriter? _writer;
    private Task? _readLoop;
    private long _nextId;
    private bool _open;

    /// <summary>Creates a transport that spawns the server from <paramref name="command"/> on <see cref="StartAsync"/>.</summary>
    public StdioMcpTransport(StdioMcpServerCommand command)
    {
        _command = command ?? throw new ArgumentNullException(nameof(command));
    }

    /// <summary>Creates a transport over an existing stream pair (tests, or a host that owns the process).</summary>
    internal StdioMcpTransport(TextReader reader, TextWriter writer)
    {
        _reader = reader;
        _writer = writer;
    }

    /// <inheritdoc />
    public event EventHandler? IsStudioOpenChanged;

    /// <inheritdoc />
    public bool IsStudioOpen
    {
        get
        {
            lock (_gate)
            {
                return _open;
            }
        }
    }

    /// <inheritdoc />
    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        await _lifecycle.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (IsStudioOpen)
            {
                return;
            }

            if (_command is not null)
            {
                SpawnProcess(_command);
            }

            _readLoop = Task.Run(ReadLoopAsync, CancellationToken.None);
            SetOpen(true);

            try
            {
                using var init = JsonDocument.Parse(
                    $"{{\"protocolVersion\":\"{_protocolVersion}\",\"capabilities\":{{}},\"clientInfo\":{{\"name\":\"bazaar-studio-bridge\",\"version\":\"0.1.0\"}}}}");
                await CallAsync("initialize", init.RootElement.Clone(), cancellationToken).ConfigureAwait(false);
                await WriteLineAsync("{\"jsonrpc\":\"2.0\",\"method\":\"notifications/initialized\"}", cancellationToken).ConfigureAwait(false);
            }
            catch
            {
                await StopCoreAsync().ConfigureAwait(false);
                throw;
            }
        }
        finally
        {
            _lifecycle.Release();
        }
    }

    /// <inheritdoc />
    public async Task StopAsync(CancellationToken cancellationToken = default)
    {
        await _lifecycle.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await StopCoreAsync().ConfigureAwait(false);
        }
        finally
        {
            _lifecycle.Release();
        }
    }

    /// <inheritdoc />
    public async Task<JsonElement> CallAsync(string method, JsonElement? parameters, CancellationToken cancellationToken = default)
    {
        if (!IsStudioOpen)
        {
            throw new StudioTransportException("The Studio MCP server is not running.");
        }

        var tcs = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        long id;
        lock (_gate)
        {
            id = ++_nextId;
            _pending[id] = tcs;
        }

        try
        {
            await WriteLineAsync(BuildRequest(id, method, parameters), cancellationToken).ConfigureAwait(false);
            return await tcs.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            lock (_gate)
            {
                _pending.Remove(id);
            }
        }
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        await StopAsync(CancellationToken.None).ConfigureAwait(false);
        _lifecycle.Dispose();
        _writeLock.Dispose();
    }

    private static string BuildRequest(long id, string method, JsonElement? parameters)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            writer.WriteString("jsonrpc", "2.0");
            writer.WriteNumber("id", id);
            writer.WriteString("method", method);
            if (parameters is { ValueKind: not (JsonValueKind.Undefined or JsonValueKind.Null) } p)
            {
                writer.WritePropertyName("params");
                p.WriteTo(writer);
            }

            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(stream.ToArray());
    }

    private void SpawnProcess(StdioMcpServerCommand command)
    {
        var info = new ProcessStartInfo(command.Command)
        {
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            StandardOutputEncoding = new UTF8Encoding(false),
            StandardInputEncoding = new UTF8Encoding(false),
        };
        foreach (var argument in command.Arguments)
        {
            info.ArgumentList.Add(argument);
        }

        if (command.WorkingDirectory is not null)
        {
            info.WorkingDirectory = command.WorkingDirectory;
        }

        Process process;
        try
        {
            process = Process.Start(info) ?? throw new StudioTransportException("The Studio MCP server did not start.");
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            throw new StudioTransportException("The Studio MCP server could not be started.", inner: ex);
        }

        // Drain stderr so a chatty server cannot block on a full pipe; its content is not used.
        process.ErrorDataReceived += static (_, _) => { };
        process.BeginErrorReadLine();
        _process = process;
        _reader = process.StandardOutput;
        _writer = process.StandardInput;
    }

    private async Task WriteLineAsync(string line, CancellationToken cancellationToken)
    {
        var writer = _writer ?? throw new StudioTransportException("The Studio MCP server is not running.");
        await _writeLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await writer.WriteLineAsync(line.AsMemory(), cancellationToken).ConfigureAwait(false);
            await writer.FlushAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException)
        {
            throw new StudioTransportException("Writing to the Studio MCP server failed.", inner: ex);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    private async Task ReadLoopAsync()
    {
        var reader = _reader!;
        try
        {
            while (await reader.ReadLineAsync().ConfigureAwait(false) is { } line)
            {
                Dispatch(line);
            }
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or InvalidOperationException)
        {
            // The pipe closed under us; treated like process exit below.
        }

        FailPending("The Studio MCP server exited.");
        SetOpen(false);
    }

    private void Dispatch(string line)
    {
        if (string.IsNullOrWhiteSpace(line))
        {
            return;
        }

        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(line);
        }
        catch (JsonException)
        {
            return; // Banner or log text on stdout; not JSON-RPC.
        }

        using (doc)
        {
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object
                || !root.TryGetProperty("id", out var idElement)
                || idElement.ValueKind != JsonValueKind.Number
                || !idElement.TryGetInt64(out var id)
                || root.TryGetProperty("method", out _))
            {
                return; // Notification or a server-initiated request; the bridge answers none.
            }

            TaskCompletionSource<JsonElement>? tcs;
            lock (_gate)
            {
                _pending.TryGetValue(id, out tcs);
            }

            if (tcs is null)
            {
                return;
            }

            if (root.TryGetProperty("error", out var error) && error.ValueKind == JsonValueKind.Object)
            {
                var code = error.TryGetProperty("code", out var c) && c.TryGetInt32(out var ci) ? ci : -32603;
                var message = error.TryGetProperty("message", out var m) && m.ValueKind == JsonValueKind.String ? m.GetString() ?? "" : "";
                tcs.TrySetException(new StudioTransportException(message, code));
            }
            else if (root.TryGetProperty("result", out var result))
            {
                tcs.TrySetResult(result.Clone());
            }
        }
    }

    private void FailPending(string reason)
    {
        List<TaskCompletionSource<JsonElement>> victims;
        lock (_gate)
        {
            victims = [.. _pending.Values];
            _pending.Clear();
        }

        foreach (var tcs in victims)
        {
            tcs.TrySetException(new StudioTransportException(reason));
        }
    }

    private void SetOpen(bool value)
    {
        lock (_gate)
        {
            if (_open == value)
            {
                return;
            }

            _open = value;
        }

        IsStudioOpenChanged?.Invoke(this, EventArgs.Empty);
    }

    private async Task StopCoreAsync()
    {
        SetOpen(false);
        FailPending("The Studio MCP server was stopped.");

        var process = _process;
        _process = null;
        if (process is not null)
        {
            try
            {
                _writer?.Close();
                if (!process.HasExited && !process.WaitForExit(2000))
                {
                    process.Kill(entireProcessTree: true);
                }
            }
            catch (Exception ex) when (ex is InvalidOperationException or IOException or System.ComponentModel.Win32Exception)
            {
                // Already gone.
            }
            finally
            {
                process.Dispose();
            }
        }

        if (_readLoop is not null)
        {
            try
            {
                await _readLoop.WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false);
            }
            catch (TimeoutException)
            {
                // The loop ends when the pipe closes; do not hang shutdown on it.
            }

            _readLoop = null;
        }

        if (_command is not null)
        {
            _reader = null;
            _writer = null;
        }
    }
}
