using System.ComponentModel;
using System.Threading.Channels;
using Bazaar.Contracts.Vms;

namespace Bazaar.Guest.Agent;

/// <summary>
/// The guest side of the host channel. It reads one host frame at a time, starts the work the frame asks for, and
/// leaves every frame it sends to <see cref="OutboundQueue"/>. <see cref="RunAsync"/> returns 0 when the host closes
/// the channel, and 1 when the agent closed it on a protocol error or a broken stream. Running work is stopped when
/// the channel ends.
/// </summary>
public sealed class GuestAgent
{
    private readonly GuestAgentOptions _options;
    private readonly IGuestTransport _transport;
    private readonly IPtyHost _ptyHost;
    private readonly TextWriter _log;
    private readonly OutboundQueue _outbound = new();
    private readonly PathGuard _paths;
    private readonly FileTransfer _files;
    private readonly Lock _gate = new();
    private readonly Dictionary<string, ExecSession> _execs = new(StringComparer.Ordinal);
    private readonly Dictionary<string, IPtySession> _ptys = new(StringComparer.Ordinal);

    /// <summary>Creates an agent. A null <paramref name="ptyHost"/> uses <see cref="PipePtyHost"/>.</summary>
    public GuestAgent(GuestAgentOptions options, IGuestTransport transport, IPtyHost? ptyHost = null, TextWriter? log = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(transport);
        _options = options;
        _transport = transport;
        _ptyHost = ptyHost ?? new PipePtyHost();
        _log = log ?? TextWriter.Null;
        _paths = new PathGuard(options.AllowedRoots);
        _files = new FileTransfer(_paths, _log);
    }

    /// <summary>Runs until the host closes the channel or a protocol error closes it. Returns the exit code.</summary>
    public async Task<int> RunAsync(CancellationToken ct)
    {
        using var runCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var token = runCts.Token;
        var writer = _outbound.RunAsync(_transport.ToHost, token);
        var heartbeats = HeartbeatAsync(token);
        var exitCode = 1;

        try
        {
            await _outbound.SendAsync(new Hello(_options.AgentVersion, _options.VmId, _options.Nonce), token);
            exitCode = await ReadLoopAsync(token);
        }
        catch (ChannelClosedException)
        {
            // The writer stopped first, so the host is gone. Nothing is left to read for.
        }
        finally
        {
            await StopAllAsync();
            _outbound.Complete();
        }

        try
        {
            await writer;
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException)
        {
            _log.WriteLine($"Guest channel write failed: {ex.Message}");
            exitCode = 1;
        }

        runCts.Cancel();
        await heartbeats;
        return exitCode;
    }

    private async Task<int> ReadLoopAsync(CancellationToken ct)
    {
        while (true)
        {
            GuestMessage? message;
            try
            {
                message = await GuestFrameCodec.ReadAsync(_transport.FromHost, ct);
            }
            catch (GuestFrameException ex)
            {
                _log.WriteLine($"Closing the channel: {ex.Message}");
                return 1;
            }

            if (message is null)
            {
                return 0;
            }

            if (!await HandleAsync(message, ct))
            {
                return 1;
            }
        }
    }

    private async Task<bool> HandleAsync(GuestMessage message, CancellationToken ct)
    {
        switch (message)
        {
            case Heartbeat:
                await _outbound.SendAsync(new Heartbeat(), ct);
                return true;
            case Exec exec:
                await StartExecAsync(exec, ct);
                return true;
            case Cancel cancel:
                CancelExec(cancel.Id);
                return true;
            case PutFile put:
                return await _files.ReceiveAsync(put, _transport.FromHost, _outbound, ct);
            case GetFile get:
                await _files.QueueSendAsync(get.Path, _outbound, ct);
                return true;
            case PtyOpen open:
                OpenPty(open);
                return true;
            case PtyData data:
                await WritePtyAsync(data, ct);
                return true;
            case PtyResize resize:
                ResizePty(resize);
                return true;
            case PtyClose close:
                await ClosePtyAsync(close.Id);
                return true;
            default:
                // Hello, Output and Exit only flow from the guest to the host. Any other frame closes the channel too.
                _log.WriteLine($"Closing the channel: {message.GetType().Name} is not a host request.");
                return false;
        }
    }

    private async Task StartExecAsync(Exec exec, CancellationToken ct)
    {
        // Only this reader adds execs, so a check made here cannot race another start. Exec tasks only remove theirs.
        var refusal = CheckExec(exec);
        if (refusal is null)
        {
            // The working directory is resolved once, and the exec runs in the resolved path, not the one the host sent.
            var cwd = _paths.ResolveDirectory(exec.Cwd);
            if (cwd is null)
            {
                refusal = new ExecRefusal(ExecRefusalCodes.CwdNotAllowed, "the working directory is outside the allowed roots");
            }
            else
            {
                var session = new ExecSession(exec with { Cwd = cwd }, _outbound);
                if (session.TryStart(out var error))
                {
                    lock (_gate)
                    {
                        _execs.Add(exec.Id, session);
                    }

                    // The continuation runs on the pool, after the entry is added, and then disposes the session.
                    _ = session.Completion.ContinueWith(_ => RemoveExec(exec.Id, session), TaskScheduler.Default);
                    return;
                }

                session.Dispose();
                refusal = new ExecRefusal(ExecRefusalCodes.ProgramNotStarted, error ?? "the program could not start");
            }
        }

        await RefuseExecAsync(exec.Id, refusal, ct);
    }

    private ExecRefusal? CheckExec(Exec exec)
    {
        if (string.IsNullOrEmpty(exec.Id))
        {
            return new ExecRefusal(ExecRefusalCodes.EmptyId, "the exec id is empty");
        }

        if (exec.Argv.Count == 0 || string.IsNullOrEmpty(exec.Argv[0]))
        {
            return new ExecRefusal(ExecRefusalCodes.NoProgram, "the exec has no program to run");
        }

        if (exec.TimeoutSec <= 0)
        {
            return new ExecRefusal(ExecRefusalCodes.TimeoutNotPositive, "the timeout must be a positive number of seconds");
        }

        // setsid starts even when the program is missing and then fails inside exec, so a missing absolute program is
        // refused here instead. A bare program name is still left to the start, which reports a failure.
        if (Path.IsPathFullyQualified(exec.Argv[0]) && !File.Exists(exec.Argv[0]))
        {
            return new ExecRefusal(ExecRefusalCodes.ProgramNotStarted, $"could not start '{exec.Argv[0]}': the file does not exist");
        }

        lock (_gate)
        {
            if (_execs.ContainsKey(exec.Id))
            {
                return new ExecRefusal(ExecRefusalCodes.DuplicateId, $"exec '{exec.Id}' is already running");
            }

            if (_execs.Count >= _options.MaxConcurrentExecs)
            {
                return new ExecRefusal(ExecRefusalCodes.TooManyExecs, $"too many concurrent execs (limit {_options.MaxConcurrentExecs})");
            }
        }

        return null;
    }

    private async Task RefuseExecAsync(string id, ExecRefusal refusal, CancellationToken ct)
    {
        _log.WriteLine($"Exec '{id}' refused: {refusal.Message}");
        await _outbound.SendAsync(new ExecError(id, refusal.Code, refusal.Message), ct);

        // The Exit that follows keeps consumers written before ExecError working: they still see the exec end.
        await _outbound.SendAsync(new Exit(id, -1, false), ct);
    }

    /// <summary>A refused exec: the machine-readable code and the human-readable reason.</summary>
    private sealed record ExecRefusal(string Code, string Message);

    private void RemoveExec(string id, ExecSession session)
    {
        lock (_gate)
        {
            _execs.Remove(id);
        }

        session.Dispose();
    }

    private void CancelExec(string id)
    {
        ExecSession? session;
        lock (_gate)
        {
            _execs.TryGetValue(id, out session);
        }

        // An unknown id is ignored on purpose: the exec may have finished just before the cancel arrived.
        session?.Cancel();
    }

    private void OpenPty(PtyOpen open)
    {
        lock (_gate)
        {
            if (_ptys.ContainsKey(open.Id))
            {
                _log.WriteLine($"PtyOpen ignored: '{open.Id}' is already open.");
                return;
            }
        }

        IPtySession session;
        try
        {
            session = _ptyHost.Open(_options.ShellPath, open.Cols, open.Rows);
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or Win32Exception or NotSupportedException)
        {
            _log.WriteLine($"PtyOpen '{open.Id}' failed: {ex.Message}");
            return;
        }

        lock (_gate)
        {
            _ptys.Add(open.Id, session);
        }

        _ = PtyPumpAsync(open.Id, session);
    }

    private async Task PtyPumpAsync(string id, IPtySession session)
    {
        long seq = 0;
        try
        {
            await foreach (var chunk in session.Output.ReadAllAsync())
            {
                for (var offset = 0; offset < chunk.Length; offset += OutboundQueue.MaxPayloadBytes)
                {
                    var length = Math.Min(OutboundQueue.MaxPayloadBytes, chunk.Length - offset);
                    await _outbound.SendAsync(new PtyData(id, seq++, chunk.AsSpan(offset, length).ToArray()), CancellationToken.None);
                }
            }

            // The output ended. A terminal the host closed was already removed by ClosePtyAsync, so only a shell that
            // ended on its own is still registered. Removing it here makes PtyExit the last frame for the terminal.
            lock (_gate)
            {
                if (!_ptys.TryGetValue(id, out var current) || !ReferenceEquals(current, session))
                {
                    return;
                }

                _ptys.Remove(id);
            }

            var code = await session.Exited;
            await session.DisposeAsync();
            await _outbound.SendAsync(new PtyExit(id, code), CancellationToken.None);
        }
        catch (ChannelClosedException)
        {
            // The host is gone.
        }
    }

    private async Task WritePtyAsync(PtyData data, CancellationToken ct)
    {
        IPtySession? session;
        lock (_gate)
        {
            _ptys.TryGetValue(data.Id, out session);
        }

        if (session is null)
        {
            _log.WriteLine($"PtyData ignored: '{data.Id}' is not open.");
            return;
        }

        try
        {
            await session.WriteAsync(data.Bytes, ct);
        }
        catch (IOException ex)
        {
            _log.WriteLine($"PtyData for '{data.Id}' could not be written: {ex.Message}");
        }
    }

    private void ResizePty(PtyResize resize)
    {
        IPtySession? session;
        lock (_gate)
        {
            _ptys.TryGetValue(resize.Id, out session);
        }

        session?.Resize(resize.Cols, resize.Rows);
    }

    private async Task ClosePtyAsync(string id)
    {
        IPtySession? session;
        lock (_gate)
        {
            _ptys.Remove(id, out session);
        }

        if (session is not null)
        {
            await session.DisposeAsync();
        }
    }

    private async Task HeartbeatAsync(CancellationToken ct)
    {
        using var timer = new PeriodicTimer(_options.HeartbeatInterval);
        try
        {
            while (await timer.WaitForNextTickAsync(ct))
            {
                await _outbound.SendAsync(new Heartbeat(), ct);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // The run is over.
        }
        catch (ChannelClosedException)
        {
            // The host is gone.
        }
    }

    private async Task StopAllAsync()
    {
        ExecSession[] execs;
        IPtySession[] ptys;
        lock (_gate)
        {
            execs = _execs.Values.ToArray();
            ptys = _ptys.Values.ToArray();
            _ptys.Clear();
        }

        foreach (var exec in execs)
        {
            exec.Cancel();
        }

        foreach (var pty in ptys)
        {
            await pty.DisposeAsync();
        }

        await Task.WhenAll(execs.Select(exec => exec.Completion));
    }
}
