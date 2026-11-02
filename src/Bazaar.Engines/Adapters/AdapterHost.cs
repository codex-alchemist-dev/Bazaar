using System.Collections.Concurrent;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using Bazaar.Contracts.Engines;

namespace Bazaar.Engines.Adapters;

/// <summary>
/// Supervises one engine adapter child process and speaks JSON-RPC 2.0 to it over stdio, one JSON message per line
/// (docs/design/20-engine-integration-api.md, Adapter contract and Behaviour and edge cases).
/// </summary>
/// <remarks>
/// <para>
/// A <c>command</c> that runs longer than <see cref="AdapterProcessOptions.CommandTimeout"/> fails the whole session:
/// the child is killed, every pending request fails with <see cref="EngineErrorCodes.Timeout"/>, and the state becomes
/// <see cref="AdapterHostState.Failed"/>. A child that exits or closes its stdout fails pending requests with
/// <see cref="EngineErrorCodes.AdapterCrashed"/>.
/// </para>
/// <para>
/// Cancelling the caller's token stops waiting for that command, sends a best-effort <c>cancel</c> request, and leaves
/// the child running. Commands are not killed by cancellation.
/// </para>
/// <para>
/// <c>seq</c> is the adapter's own stream counter per project (the <c>output</c> notifications). The host drops an
/// output message whose seq is not greater than the last one seen for that project. GC sequence numbers are not used here.
/// </para>
/// </remarks>
public sealed class AdapterHost : IAsyncDisposable
{
    /// <summary>How long a best-effort cancel waits for the adapter's reply before the host stops waiting for it.</summary>
    private static readonly TimeSpan _cancelGrace = TimeSpan.FromSeconds(2);

    /// <summary>How long the host waits for the child to exit after a kill or close, and for the readers to drain.</summary>
    private static readonly TimeSpan _exitWait = TimeSpan.FromSeconds(5);

    /// <summary>Short wait so the stdout reader can drain what the child wrote just before it exited.</summary>
    private static readonly TimeSpan _drainWait = TimeSpan.FromSeconds(1);

    private const int _invalidResponseCode = -32603;

    private readonly Process _process;
    private readonly AdapterProcessOptions _options;
    private readonly ConcurrentDictionary<string, TaskCompletionSource<JsonElement?>> _pending = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim _writeGate = new(1, 1);
    private readonly object _gate = new();
    private readonly Queue<string> _tail = new();
    private readonly Dictionary<string, long> _lastSeq = new(StringComparer.Ordinal);
    private readonly Task _stdoutLoop;
    private readonly Task _stderrLoop;
    private long _nextRequestId;

    // Guarded by _gate.
    private AdapterHostState _state = AdapterHostState.Running;
    private EngineAdapterException? _failure;

    private AdapterHost(Process process, AdapterProcessOptions options)
    {
        _process = process;
        _options = options;
        _stdoutLoop = ReadStdoutAsync();
        _stderrLoop = ReadStderrAsync();
        _ = ObserveExitAsync();
    }

    /// <summary>Raised on the reader thread for each accepted <c>output</c> notification, in arrival order.</summary>
    public event Action<AdapterOutput>? OutputReceived;

    /// <summary>The current lifecycle state.</summary>
    public AdapterHostState State
    {
        get
        {
            lock (_gate)
            {
                return _state;
            }
        }
    }

    /// <summary>The child's exit code once it has exited; null while it runs.</summary>
    public int? ExitCode => _process.HasExited ? _process.ExitCode : null;

    /// <summary>Starts the adapter child process. The process is not spoken to until a request is sent.</summary>
    public static AdapterHost Start(AdapterProcessOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentException.ThrowIfNullOrWhiteSpace(options.FileName);
        ArgumentNullException.ThrowIfNull(options.Arguments);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(options.CommandTimeout, TimeSpan.Zero);
        ArgumentOutOfRangeException.ThrowIfLessThan(options.OutputTailLines, 1);

        var startInfo = new ProcessStartInfo(options.FileName)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardInputEncoding = new UTF8Encoding(false),
            StandardOutputEncoding = new UTF8Encoding(false),
            StandardErrorEncoding = new UTF8Encoding(false),
        };
        foreach (var argument in options.Arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }
        if (options.WorkingDirectory is not null)
        {
            startInfo.WorkingDirectory = options.WorkingDirectory;
        }

        var process = Process.Start(startInfo) ?? throw new InvalidOperationException($"Could not start adapter '{options.FileName}'.");
        process.StandardInput.AutoFlush = false;
        return new AdapterHost(process, options);
    }

    /// <summary>Sends <c>open</c> for a project.</summary>
    public Task<JsonElement?> OpenAsync(string projectId, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projectId);
        return RequestAsync(EngineMethods.Open, Serialize(new AdapterProjectParams(projectId)), ct);
    }

    /// <summary>Sends <c>health</c>. The adapter answers with its own status object.</summary>
    public Task<JsonElement?> HealthAsync(CancellationToken ct) => RequestAsync(EngineMethods.Health, null, ct);

    /// <summary>
    /// Sends one <c>command</c> and waits for its result. The command timeout applies to this call. A timeout kills the
    /// child and fails the session. If <paramref name="ct"/> is cancelled, the call throws <see cref="OperationCanceledException"/>
    /// and the child keeps running.
    /// </summary>
    public async Task<CommandResult> CommandAsync(string projectId, EngineCommand command, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projectId);
        ArgumentNullException.ThrowIfNull(command);

        var parameters = Serialize(new AdapterCommandParams(
            projectId,
            command.CommandId,
            LastSeq(projectId),
            command.Name,
            command.Arguments));

        using var timeout = new CancellationTokenSource(_options.CommandTimeout);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, timeout.Token);
        JsonElement? result;
        try
        {
            result = await RequestAsync(EngineMethods.Command, parameters, linked.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested && timeout.IsCancellationRequested)
        {
            var failure = Fail(
                EngineErrorCodes.Timeout,
                $"Command '{command.Name}' ({command.CommandId}) exceeded {_options.CommandTimeout}; the adapter was killed.");
            await WaitForExitAsync().ConfigureAwait(false);
            throw failure.Copy();
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            _ = SendCancelQuietlyAsync(projectId, command.CommandId);
            throw;
        }

        if (result is not { ValueKind: not JsonValueKind.Null and not JsonValueKind.Undefined } value)
        {
            throw new EngineAdapterException(_invalidResponseCode, $"Command '{command.CommandId}' returned no result.", RecentOutput());
        }
        try
        {
            return value.Deserialize<CommandResult>(EngineJson.Options)
                ?? throw new EngineAdapterException(_invalidResponseCode, $"Command '{command.CommandId}' returned a null result.", RecentOutput());
        }
        catch (JsonException ex)
        {
            throw new EngineAdapterException(_invalidResponseCode, $"Command '{command.CommandId}' returned a malformed result: {ex.Message}", RecentOutput());
        }
    }

    /// <summary>Sends <c>cancel</c> for a command. The adapter decides what cancelling means for its engine.</summary>
    public Task<JsonElement?> CancelAsync(string projectId, string commandId, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projectId);
        ArgumentException.ThrowIfNullOrWhiteSpace(commandId);
        return RequestAsync(EngineMethods.Cancel, Serialize(new EngineProjectEnvelope(projectId, commandId, LastSeq(projectId))), ct);
    }

    /// <summary>
    /// Sends <c>close</c> for a project. A successful close lets the child exit on its own; the host waits for that exit
    /// and kills the child only if it does not come in time.
    /// </summary>
    public async Task<JsonElement?> CloseAsync(string projectId, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projectId);
        var result = await RequestAsync(EngineMethods.Close, Serialize(new AdapterProjectParams(projectId)), ct).ConfigureAwait(false);
        lock (_gate)
        {
            if (_state == AdapterHostState.Running)
            {
                _state = AdapterHostState.Closed;
            }
        }
        await WaitForExitAsync().ConfigureAwait(false);
        return result;
    }

    /// <summary>The last <see cref="AdapterProcessOptions.OutputTailLines"/> output lines, oldest first.</summary>
    public IReadOnlyList<string> RecentOutput()
    {
        lock (_gate)
        {
            return _tail.ToArray();
        }
    }

    /// <summary>The last accepted output seq for a project, or 0 if none has arrived.</summary>
    public long LastSeq(string projectId)
    {
        lock (_gate)
        {
            return _lastSeq.TryGetValue(projectId, out var seq) ? seq : 0;
        }
    }

    public async ValueTask DisposeAsync()
    {
        lock (_gate)
        {
            if (_state == AdapterHostState.Running)
            {
                _state = AdapterHostState.Closed;
            }
        }
        foreach (var id in _pending.Keys)
        {
            if (_pending.TryRemove(id, out var waiter))
            {
                waiter.TrySetException(new EngineAdapterException(EngineErrorCodes.AdapterUnavailable, "The adapter host was disposed."));
            }
        }
        KillProcess();
        await WaitForExitAsync().ConfigureAwait(false);
        await Task.WhenAll(_stdoutLoop, _stderrLoop).WaitAsync(_drainWait).ConfigureAwait(false);
        _process.StandardInput.Dispose();
        _process.Dispose();
        _writeGate.Dispose();
    }

    private static JsonElement Serialize<T>(T value) => JsonSerializer.SerializeToElement(value, EngineJson.Options);

    private async Task<JsonElement?> RequestAsync(string method, JsonElement? parameters, CancellationToken ct)
    {
        var id = Interlocked.Increment(ref _nextRequestId).ToString(CultureInfo.InvariantCulture);
        var waiter = new TaskCompletionSource<JsonElement?>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending[id] = waiter;
        try
        {
            // Registered before the state check: a failure that runs after this point either sees the entry and
            // completes it, or has already set the state, which EnsureRunning reports.
            EnsureRunning();
            await WriteAsync(new JsonRpcRequest(id, method, parameters), ct).ConfigureAwait(false);
            return await waiter.Task.WaitAsync(ct).ConfigureAwait(false);
        }
        finally
        {
            _pending.TryRemove(id, out _);
        }
    }

    private void EnsureRunning()
    {
        lock (_gate)
        {
            if (_state == AdapterHostState.Running)
            {
                return;
            }
            var reason = _failure?.Message ?? "The adapter session is closed.";
            throw new EngineAdapterException(EngineErrorCodes.AdapterUnavailable, $"The adapter is not running: {reason}", _tail.ToArray());
        }
    }

    private async Task WriteAsync(JsonRpcRequest request, CancellationToken ct)
    {
        var line = JsonSerializer.Serialize(request, EngineJson.Options);
        await _writeGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await _process.StandardInput.WriteLineAsync(line.AsMemory(), ct).ConfigureAwait(false);
            await _process.StandardInput.FlushAsync(ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException)
        {
            throw Fail(EngineErrorCodes.AdapterCrashed, $"The adapter's input closed while sending '{request.Method}': {ex.Message}").Copy();
        }
        finally
        {
            _writeGate.Release();
        }
    }

    private async Task SendCancelQuietlyAsync(string projectId, string commandId)
    {
        try
        {
            await CancelAsync(projectId, commandId, CancellationToken.None).WaitAsync(_cancelGrace).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is EngineAdapterException or TimeoutException or OperationCanceledException)
        {
            // Best effort: the caller already stopped waiting, and the session state reports any real failure.
        }
    }

    /// <summary>
    /// Moves the session to Failed, fails every pending request with a copy of the same error, and kills the child.
    /// The first failure wins; later calls return it unchanged.
    /// </summary>
    private EngineAdapterException Fail(int code, string message)
    {
        EngineAdapterException failure;
        lock (_gate)
        {
            if (_state == AdapterHostState.Failed && _failure is not null)
            {
                return _failure;
            }
            if (_state == AdapterHostState.Closed)
            {
                return new EngineAdapterException(EngineErrorCodes.AdapterUnavailable, "The adapter session is closed.", _tail.ToArray());
            }
            _state = AdapterHostState.Failed;
            _failure = new EngineAdapterException(code, message, _tail.ToArray());
            failure = _failure;
        }

        foreach (var id in _pending.Keys)
        {
            if (_pending.TryRemove(id, out var waiter))
            {
                waiter.TrySetException(failure.Copy());
            }
        }
        KillProcess();
        return failure;
    }

    private void KillProcess()
    {
        try
        {
            if (!_process.HasExited)
            {
                _process.Kill(entireProcessTree: true);
            }
        }
        catch (Exception ex) when (ex is InvalidOperationException or Win32Exception)
        {
            // The child exited between the check and the kill. Nothing left to stop.
        }
    }

    private async Task WaitForExitAsync()
    {
        try
        {
            await _process.WaitForExitAsync().WaitAsync(_exitWait).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            KillProcess();
        }
    }

    private async Task ReadStdoutAsync()
    {
        try
        {
            string? line;
            while ((line = await _process.StandardOutput.ReadLineAsync().ConfigureAwait(false)) is not null)
            {
                HandleLine(line);
            }
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException)
        {
            // Treated as end of stream below.
        }

        // End of stdout: the child closed it, or it is about to exit. Wait briefly so the message can carry the exit code.
        try
        {
            await _process.WaitForExitAsync().WaitAsync(_drainWait).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            // Still running with stdout closed: treated as a crash below.
        }
        Fail(EngineErrorCodes.AdapterCrashed, DescribeEnd());
    }

    private async Task ReadStderrAsync()
    {
        try
        {
            string? line;
            while ((line = await _process.StandardError.ReadLineAsync().ConfigureAwait(false)) is not null)
            {
                AddTail(line);
            }
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException)
        {
            // The stream closed with the child.
        }
    }

    private async Task ObserveExitAsync()
    {
        await _process.WaitForExitAsync().ConfigureAwait(false);
        // Let the readers drain the child's last lines so the failure carries them.
        try
        {
            await _stdoutLoop.WaitAsync(_drainWait).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            // The stdout reader will report the failure itself.
        }
        Fail(EngineErrorCodes.AdapterCrashed, DescribeEnd());
    }

    private string DescribeEnd() => _process.HasExited
        ? $"The adapter exited with code {_process.ExitCode}."
        : "The adapter closed its output while still running.";

    private void HandleLine(string line)
    {
        if (string.IsNullOrWhiteSpace(line))
        {
            return;
        }

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(line);
        }
        catch (JsonException)
        {
            AddTail($"[malformed message dropped] {line}");
            return;
        }

        using (document)
        {
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return;
            }

            if (root.TryGetProperty("method", out var method) && method.ValueKind == JsonValueKind.String)
            {
                // Adapter-originated requests are not part of protocol v1; only the output notification is handled.
                if (!root.TryGetProperty("id", out _) && method.GetString() == EngineMethods.Output)
                {
                    HandleOutput(root);
                }
                return;
            }

            if (root.TryGetProperty("id", out var id) && id.ValueKind == JsonValueKind.String)
            {
                HandleResponse(id.GetString()!, line);
            }
        }
    }

    private void HandleResponse(string id, string line)
    {
        // A response with no waiter is a late reply to a request that already timed out or was cancelled.
        if (!_pending.TryRemove(id, out var waiter))
        {
            return;
        }

        JsonRpcResponse? response;
        try
        {
            response = JsonSerializer.Deserialize<JsonRpcResponse>(line, EngineJson.Options);
        }
        catch (JsonException ex)
        {
            waiter.TrySetException(new EngineAdapterException(_invalidResponseCode, $"Malformed response: {ex.Message}", RecentOutput()));
            return;
        }

        if (response?.Error is { } error)
        {
            waiter.TrySetException(new EngineAdapterException(error.Code, error.Message, RecentOutput()));
            return;
        }
        waiter.TrySetResult(response?.Result);
    }

    private void HandleOutput(JsonElement root)
    {
        if (!root.TryGetProperty("params", out var parameters) || parameters.ValueKind != JsonValueKind.Object)
        {
            return;
        }

        EngineProjectEnvelope? envelope;
        try
        {
            envelope = parameters.Deserialize<EngineProjectEnvelope>(EngineJson.Options);
        }
        catch (JsonException)
        {
            return;
        }
        if (envelope is null)
        {
            return;
        }

        var text = parameters.TryGetProperty("text", out var textElement) && textElement.ValueKind == JsonValueKind.String
            ? textElement.GetString() ?? string.Empty
            : string.Empty;

        lock (_gate)
        {
            // Duplicate or out-of-order output is dropped so the per-project stream stays monotonic.
            if (_lastSeq.TryGetValue(envelope.ProjectId, out var last) && envelope.Seq <= last)
            {
                return;
            }
            _lastSeq[envelope.ProjectId] = envelope.Seq;
            AddTailLocked(text);
        }

        OutputReceived?.Invoke(new AdapterOutput(envelope.ProjectId, envelope.CommandId, envelope.Seq, text));
    }

    private void AddTail(string line)
    {
        lock (_gate)
        {
            AddTailLocked(line);
        }
    }

    private void AddTailLocked(string line)
    {
        _tail.Enqueue(line);
        while (_tail.Count > _options.OutputTailLines)
        {
            _tail.Dequeue();
        }
    }
}
