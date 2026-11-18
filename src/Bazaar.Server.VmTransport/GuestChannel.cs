using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Channels;
using Bazaar.Contracts.Vms;

namespace Bazaar.Server.VmTransport;

/// <summary>
/// The host end of one guest connection (doc 16 <see cref="IGuestChannel"/>). It speaks the guest frame codec over the accepted stream. The first
/// frame must be a Hello with the expected VM id and the server-issued nonce, or the connection is closed. One reader task dispatches every guest
/// frame; writes are serialised. An unknown frame type, a guest frame that only the host may send, or a broken stream ends the channel
/// (<see cref="GuestChannelState.Faulted"/>). Three silent heartbeat intervals end it as <see cref="GuestChannelState.Lost"/>.
/// </summary>
public sealed class GuestChannel : IGuestChannel, IAsyncDisposable
{
    private const int _workspaceOpsProtocolVersion = 3;
    private const int _copyBytes = 64 * 1024;
    private const int _ptyChunkBytes = 32 * 1024;

    private readonly GuestConnection _connection;
    private readonly Stream _stream;
    private readonly GuestChannelOptions _options;
    private readonly TimeProvider _time;
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private readonly SemaphoreSlim _fileGate = new(1, 1);
    private readonly Lock _gate = new();
    private readonly Dictionary<string, ExecState> _execs = new(StringComparer.Ordinal);
    private readonly Dictionary<string, PtyState> _ptys = new(StringComparer.Ordinal);
    private readonly CancellationTokenSource _stop = new();
    private readonly TaskCompletionSource _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private PendingFile? _file;
    private int _state = (int)GuestChannelState.Open;
    private string? _reason;
    private int _seen = 1;
    private Task _reader = Task.CompletedTask;
    private Task _watchdog = Task.CompletedTask;

    private GuestChannel(GuestConnection connection, string agentVersion, int protocolVersion, GuestChannelOptions options, TimeProvider time)
    {
        ProtocolVersion = protocolVersion;
        _connection = connection;
        _stream = connection.Stream;
        AgentVersion = agentVersion;
        _options = options;
        _time = time;
    }

    /// <summary>The highest guest protocol version the agent announced in its Hello; 0 for an agent that predates the field.</summary>
    public int ProtocolVersion { get; }

    /// <summary>The agent build the guest announced in its Hello.</summary>
    public string AgentVersion { get; }

    /// <summary>Where the channel is in its life.</summary>
    public GuestChannelState State => (GuestChannelState)Volatile.Read(ref _state);

    /// <summary>Why the channel ended, or null while it is open.</summary>
    public string? EndReason
    {
        get
        {
            lock (_gate)
            {
                return _reason;
            }
        }
    }

    /// <summary>Completes when the channel has ended, in any state. Never faults.</summary>
    public Task Completion => _completion.Task;

    /// <summary>
    /// Reads the guest's Hello from <paramref name="connection"/> and, if it matches <paramref name="expected"/>, returns an open channel.
    /// On any mismatch, a missing Hello, an unknown frame or a timeout the connection is closed and <see cref="GuestHandshakeException"/> is thrown.
    /// The channel owns the connection from here on.
    /// </summary>
    public static async Task<GuestChannel> OpenAsync(
        GuestConnection connection,
        GuestHandshake expected,
        GuestChannelOptions? options = null,
        TimeProvider? timeProvider = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(expected);
        var settings = options ?? new GuestChannelOptions();
        var time = timeProvider ?? TimeProvider.System;

        try
        {
            if (string.IsNullOrEmpty(expected.VmId) || string.IsNullOrEmpty(expected.Nonce))
            {
                throw new GuestHandshakeException("The expected VM id and nonce must both be set, or the Hello check proves nothing.");
            }

            var hello = await ReadHelloAsync(connection.Stream, settings, time, ct);
            if (hello.VmId is null || hello.Nonce is null || hello.AgentVersion is null)
            {
                throw new GuestHandshakeException("The guest's Hello is missing a field.");
            }

            if (!string.Equals(hello.VmId, expected.VmId, StringComparison.Ordinal))
            {
                throw new GuestHandshakeException("The guest's Hello names a different VM than the one expected.");
            }

            if (!CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(hello.Nonce), Encoding.UTF8.GetBytes(expected.Nonce)))
            {
                throw new GuestHandshakeException("The guest's Hello carries the wrong nonce.");
            }

            var channel = new GuestChannel(connection, hello.AgentVersion, hello.ProtocolVersion, settings, time);
            channel._reader = Task.Run(channel.ReadLoopAsync, CancellationToken.None);
            // The timer is created here, not in the task, so the first interval starts at open and not whenever the pool runs the task.
            var timer = new PeriodicTimer(settings.HeartbeatInterval, time);
            channel._watchdog = Task.Run(() => channel.WatchdogAsync(timer), CancellationToken.None);
            return channel;
        }
        catch
        {
            await connection.DisposeAsync();
            throw;
        }
    }

    /// <inheritdoc />
    public async Task<ExecResult> ExecAsync(ExecRequest req, Func<OutputChunk, Task> onOutput, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(req);
        ArgumentNullException.ThrowIfNull(onOutput);
        if (req.Argv.Count == 0 || string.IsNullOrEmpty(req.Argv[0]))
        {
            throw new ArgumentException("The exec has no program to run.", nameof(req));
        }

        if (req.Timeout <= TimeSpan.Zero || req.Timeout > _options.MaxExecTimeout)
        {
            throw new ArgumentOutOfRangeException(nameof(req), $"The exec timeout must be above zero and at most {_options.MaxExecTimeout}.");
        }

        ct.ThrowIfCancellationRequested();
        ThrowIfEnded();

        var id = Guid.NewGuid().ToString("N");
        var exec = new ExecState(id, _options.MaxOutputBytes);
        lock (_gate)
        {
            if (_execs.Count >= _options.MaxConcurrentExecs)
            {
                throw new GuestExecRefusedException(ExecRefusalCodes.TooManyExecs, $"too many concurrent execs (limit {_options.MaxConcurrentExecs})");
            }

            _execs.Add(id, exec);
        }

        var consumer = exec.ConsumeAsync(onOutput, () => TrySendCancelAsync(id));
        try
        {
            var message = new Exec(id, [.. req.Argv], req.Cwd, [.. req.EnvAllowlist], (int)Math.Ceiling(req.Timeout.TotalSeconds));
            await SendAsync(message, ct);
            var result = await AwaitExitAsync(exec, req.Timeout, ct);
            exec.Chunks.Writer.TryComplete();
            await consumer;
            exec.CallbackFailure?.Throw();
            return result;
        }
        finally
        {
            lock (_gate)
            {
                _execs.Remove(id);
            }

            exec.Chunks.Writer.TryComplete();
            await consumer.ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
        }
    }

    /// <inheritdoc />
    public async Task PutFileAsync(string guestPath, Stream content, string sha256, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrEmpty(guestPath);
        ArgumentNullException.ThrowIfNull(content);
        ArgumentException.ThrowIfNullOrEmpty(sha256);
        if (!content.CanSeek)
        {
            throw new ArgumentException("The content stream must be seekable: the wire announces the size before the bytes.", nameof(content));
        }

        if (sha256.Length != 64 || !sha256.All(char.IsAsciiHexDigit))
        {
            throw new ArgumentException("The SHA-256 must be 64 hex characters.", nameof(sha256));
        }

        var size = content.Length - content.Position;
        if (size > _options.MaxFileBytes)
        {
            throw new GuestFileException($"The file is {size} bytes, over the {_options.MaxFileBytes}-byte limit.");
        }

        var pending = await BeginFileAsync(PendingFile.Put(guestPath), ct);
        try
        {
            await _writeLock.WaitAsync(ct);
            try
            {
                ThrowIfEnded();
                await WriteFrameAsync(new PutFile(guestPath, size, sha256.ToLowerInvariant()));
                await CopyContentAsync(content, size, ct);
            }
            finally
            {
                _writeLock.Release();
            }
        }
        catch
        {
            FinishFile(pending);
            throw;
        }

        await WaitFileAsync(pending, ct);
    }

    /// <inheritdoc />
    public async Task GetFileAsync(string guestPath, Stream sink, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrEmpty(guestPath);
        ArgumentNullException.ThrowIfNull(sink);

        var pending = await BeginFileAsync(PendingFile.Get(guestPath, sink), ct);
        try
        {
            await SendAsync(new GetFile(guestPath), ct);
        }
        catch
        {
            FinishFile(pending);
            throw;
        }

        await WaitFileAsync(pending, ct);
    }

    /// <inheritdoc />
    public async Task DeleteFileAsync(string relativePath, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrEmpty(relativePath);
        CheckWorkspaceRequest(relativePath);
        await RoundTripAsync(PendingKind.Delete, relativePath, new DeleteFile(relativePath), ct);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<GuestDirectoryEntry>> ListFilesAsync(string relativeDir, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(relativeDir);
        CheckWorkspaceRequest(relativeDir);
        var reply = (ListFilesResult)await RoundTripAsync(PendingKind.List, relativeDir, new ListFiles(relativeDir), ct);
        return [.. (reply.Entries ?? []).Select(entry => new GuestDirectoryEntry(entry.Name, entry.Size, entry.IsDirectory))];
    }

    /// <inheritdoc />
    public async Task<string?> ResolveRealPathAsync(string relativePath, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrEmpty(relativePath);
        CheckWorkspaceRequest(relativePath);
        return ((ResolveRealPathResult)await RoundTripAsync(PendingKind.Resolve, relativePath, new ResolveRealPath(relativePath), ct)).RealPath;
    }

    /// <inheritdoc />
    public async Task<IGuestPty> OpenPtyAsync(int cols, int rows, CancellationToken ct)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(cols);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(rows);
        ThrowIfEnded();

        var id = Guid.NewGuid().ToString("N");
        var pty = new PtyState(this, id, _options.MaxPendingPtyBytes);
        lock (_gate)
        {
            if (_ptys.Count >= _options.MaxOpenPtys)
            {
                throw new InvalidOperationException($"Too many open terminals on this channel (limit {_options.MaxOpenPtys}).");
            }

            _ptys.Add(id, pty);
        }

        try
        {
            await SendAsync(new PtyOpen(id, cols, rows), ct);
        }
        catch
        {
            lock (_gate)
            {
                _ptys.Remove(id);
            }

            throw;
        }

        return pty;
    }

    /// <summary>Ends the channel and closes the connection. The guest sees the stream end and exits. Safe to call twice.</summary>
    public async ValueTask DisposeAsync()
    {
        End(GuestChannelState.Closed, "the host closed the channel");
        await Task.WhenAll(_reader, _watchdog).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
    }

    private static async Task<Hello> ReadHelloAsync(Stream stream, GuestChannelOptions options, TimeProvider time, CancellationToken ct)
    {
        using var timeout = new CancellationTokenSource(options.HelloTimeout, time);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, timeout.Token);
        GuestMessage? first;
        try
        {
            first = await GuestFrameCodec.ReadAsync(stream, linked.Token);
        }
        catch (OperationCanceledException) when (timeout.IsCancellationRequested && !ct.IsCancellationRequested)
        {
            throw new GuestHandshakeException("The guest did not send its Hello in time.");
        }
        catch (GuestFrameException ex)
        {
            throw new GuestHandshakeException($"The first frame was not valid: {ex.Message}");
        }

        return first switch
        {
            null => throw new GuestHandshakeException("The guest closed the connection before its Hello."),
            Hello hello => hello,
            _ => throw new GuestHandshakeException($"The first frame was {first.GetType().Name}, not Hello."),
        };
    }

    private async Task<ExecResult> AwaitExitAsync(ExecState exec, TimeSpan timeout, CancellationToken ct)
    {
        Exit? exit = null;
        var cancelled = false;
        var timedOutHere = false;
        try
        {
            exit = await exec.Exited.Task.WaitAsync(timeout + _options.TimeoutGrace, _time, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            cancelled = true;
        }
        catch (TimeoutException)
        {
            timedOutHere = true;
        }

        if (exit is null)
        {
            // Ask the guest to kill the tree, then give it a short grace to report the Exit. A guest that never answers is given up on.
            await TrySendCancelAsync(exec.Id);
            try
            {
                exit = await exec.Exited.Task.WaitAsync(_options.CancelGrace, _time, CancellationToken.None);
            }
            catch (TimeoutException)
            {
                // The exec is abandoned; frames it still produces are ignored because its id is no longer known.
            }
        }

        var truncated = exec.Truncated;
        // Our own deadline passing counts as a timeout even when the guest's Exit, which only came after the Cancel, does not say so.
        return exit is null
            ? new ExecResult(-1, timedOutHere, cancelled, truncated)
            : new ExecResult(exit.Code, exit.TimedOut || timedOutHere, cancelled && !(exit.TimedOut || timedOutHere), truncated);
    }

    private async Task TrySendPtyCloseAsync(string id)
    {
        try
        {
            using var bound = new CancellationTokenSource(_options.CancelGrace, _time);
            await SendAsync(new PtyClose(id), bound.Token);
        }
        catch (Exception ex) when (ex is GuestChannelException or IOException or OperationCanceledException)
        {
            // The channel is gone or stuck; the guest's terminal goes with it.
        }
    }

    private async Task TrySendCancelAsync(string id)
    {
        try
        {
            // The lock wait is bounded: a frame stuck in front of the Cancel must not stall the caller past its grace.
            using var bound = new CancellationTokenSource(_options.CancelGrace, _time);
            await SendAsync(new Cancel(id), bound.Token);
        }
        catch (Exception ex) when (ex is GuestChannelException or IOException or OperationCanceledException)
        {
            // The channel is gone, so the exec is lost anyway and its waiter fails on its own.
        }
    }

    private async Task<PendingFile> BeginFileAsync(PendingFile pending, CancellationToken ct)
    {
        await _fileGate.WaitAsync(ct);
        lock (_gate)
        {
            if (State != GuestChannelState.Open)
            {
                _fileGate.Release();
                throw EndedException();
            }

            pending.Gate = _fileGate;
            _file = pending;
        }

        return pending;
    }

    /// <summary>
    /// Refuses a workspace file request the guest cannot understand: a path over the length limit, or an agent that predates protocol
    /// version 3 (it would close the channel on the unknown tag instead of answering).
    /// </summary>
    private void CheckWorkspaceRequest(string path)
    {
        if (path.Length > GuestPathRules.MaxPathChars)
        {
            throw new ArgumentException($"The path is over the {GuestPathRules.MaxPathChars}-character limit.", nameof(path));
        }

        if (ProtocolVersion < _workspaceOpsProtocolVersion)
        {
            throw new NotSupportedException(
                $"The guest agent '{AgentVersion}' speaks guest protocol version {ProtocolVersion}; DeleteFile, ListFiles and ResolveRealPath need version {_workspaceOpsProtocolVersion}. Update the guest agent in the golden image.");
        }
    }

    /// <summary>One request and its single answer, serialised with the file transfers because the guest answers by path.</summary>
    private async Task<GuestMessage> RoundTripAsync(PendingKind kind, string path, GuestMessage request, CancellationToken ct)
    {
        var pending = await BeginFileAsync(PendingFile.Request(kind, path), ct);
        try
        {
            await SendAsync(request, ct);
        }
        catch
        {
            FinishFile(pending);
            throw;
        }

        await WaitFileAsync(pending, ct);
        return pending.Reply!;
    }

    /// <summary>Completes the waiting request of <paramref name="kind"/> for <paramref name="path"/> with the guest's answer.</summary>
    private bool CompleteRequest(PendingKind kind, string path, GuestMessage reply, string? error)
    {
        var pending = CurrentFile(kind, path);
        if (pending is null)
        {
            End(GuestChannelState.Faulted, $"the guest sent {reply.GetType().Name} nobody asked for.");
            return false;
        }

        if (error is null)
        {
            pending.Reply = reply;
            pending.Done.TrySetResult();
        }
        else
        {
            pending.Done.TrySetException(new GuestFileException(error));
        }

        FinishFile(pending);
        return true;
    }

    private async Task WaitFileAsync(PendingFile pending, CancellationToken ct)
    {
        try
        {
            while (true)
            {
                try
                {
                    await pending.Done.Task.WaitAsync(_options.FileAnswerTimeout, _time, ct);
                    return;
                }
                catch (TimeoutException) when (pending.Receiving)
                {
                    // Content is on its way; the heartbeat watchdog covers a stall from here.
                }
                catch (TimeoutException)
                {
                    End(GuestChannelState.Faulted, $"the guest did not answer a file request within {_options.FileAnswerTimeout}");
                    await pending.Done.Task;
                }
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // The guest still answers. The reader keeps the stream in step and releases the gate when the answer is in.
            pending.Abandon();
            throw;
        }
    }

    private async Task CopyContentAsync(Stream content, long size, CancellationToken ct)
    {
        var buffer = new byte[_copyBytes];
        var remaining = size;
        try
        {
            while (remaining > 0)
            {
                var read = await content.ReadAsync(buffer.AsMemory(0, (int)Math.Min(buffer.Length, remaining)), ct);
                if (read == 0)
                {
                    throw new IOException("The content stream ended before its announced size.");
                }

                // Each slice has its own deadline, so a guest that stops reading is noticed within one WriteTimeout.
                using var deadline = WriteDeadline();
                await _stream.WriteAsync(buffer.AsMemory(0, read), deadline.Token);
                remaining -= read;
            }

            using var flushDeadline = WriteDeadline();
            await _stream.FlushAsync(flushDeadline.Token);
        }
        catch (Exception ex) when (ex is IOException or OperationCanceledException or ObjectDisposedException or SocketException)
        {
            // The header is out, so the guest waits for bytes that will not come and the stream cannot be recovered. No other frame may be
            // slipped in between slices for the same reason: the guest reads the announced size as raw content.
            End(GuestChannelState.Faulted, $"a file transfer broke off mid-content: {ex.Message}");
            if (ex is OperationCanceledException && !ct.IsCancellationRequested)
            {
                throw EndedException();
            }

            throw;
        }
    }

    private ValueTask SendAsync(GuestMessage message, CancellationToken ct) => SendAsync(() => message, ct);

    /// <summary>Builds the message only once the write lock is held, so anything numbered in it is numbered in wire order.</summary>
    private async ValueTask SendAsync(Func<GuestMessage> make, CancellationToken ct)
    {
        ThrowIfEnded();
        await _writeLock.WaitAsync(ct);
        try
        {
            ThrowIfEnded();
            await WriteFrameAsync(make());
        }
        finally
        {
            _writeLock.Release();
        }
    }

    /// <summary>Writes one frame under the channel's own token, never a caller's: cancelling halfway through a frame would corrupt the stream.</summary>
    private async ValueTask WriteFrameAsync(GuestMessage message)
    {
        try
        {
            using var deadline = WriteDeadline();
            await GuestFrameCodec.WriteAsync(_stream, message, deadline.Token);
        }
        catch (Exception ex) when (ex is IOException or OperationCanceledException or ObjectDisposedException or SocketException)
        {
            End(GuestChannelState.Faulted, ex is OperationCanceledException && !_stop.IsCancellationRequested
                ? $"the guest stopped reading: a write took longer than {_options.WriteTimeout}"
                : $"the stream failed on write: {ex.Message}");
            throw EndedException();
        }
    }

    /// <summary>A token that fires when the channel ends or one write has run past <see cref="GuestChannelOptions.WriteTimeout"/>.</summary>
    private Deadline WriteDeadline() => new(_options.WriteTimeout, _time, _stop.Token);

    /// <summary>A timeout on the channel's own clock, joined to the channel's stop token.</summary>
    private sealed class Deadline : IDisposable
    {
        private readonly CancellationTokenSource _timeout;
        private readonly CancellationTokenSource _linked;

        public Deadline(TimeSpan after, TimeProvider time, CancellationToken stop)
        {
            _timeout = new CancellationTokenSource(after, time);
            _linked = CancellationTokenSource.CreateLinkedTokenSource(stop, _timeout.Token);
        }

        public CancellationToken Token => _linked.Token;

        public void Dispose()
        {
            _linked.Dispose();
            _timeout.Dispose();
        }
    }

    private async Task ReadLoopAsync()
    {
        try
        {
            while (true)
            {
                var message = await GuestFrameCodec.ReadAsync(_stream, _stop.Token);
                if (message is null)
                {
                    End(GuestChannelState.Closed, "the guest closed the channel");
                    return;
                }

                Volatile.Write(ref _seen, 1);
                if (!await DispatchAsync(message))
                {
                    return;
                }
            }
        }
        catch (GuestFrameException ex)
        {
            End(GuestChannelState.Faulted, ex.Message);
        }
        catch (Exception ex) when (ex is IOException or OperationCanceledException or ObjectDisposedException or SocketException)
        {
            // Raised by our own End closing the stream, or by a real break. End keeps the first reason.
            End(GuestChannelState.Faulted, $"the stream failed on read: {ex.Message}");
        }
#pragma warning disable CA1031 // The reader is the channel's only reader: whatever a bad frame makes it throw (a null field, say) must end the channel, never leave it open and unread.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            End(GuestChannelState.Faulted, $"a frame could not be handled ({ex.GetType().Name}): {ex.Message}");
        }
    }

    private async ValueTask<bool> DispatchAsync(GuestMessage message)
    {
        switch (message)
        {
            case Heartbeat:
                return true;
            case Output output:
                ExecState? exec;
                lock (_gate)
                {
                    _execs.TryGetValue(output.Id, out exec);
                }

                if (output.Bytes is null)
                {
                    End(GuestChannelState.Faulted, "an Output frame carried no bytes.");
                    return false;
                }

                exec?.AddOutput(output);
                return true;
            case Exit exit:
                lock (_gate)
                {
                    _execs.TryGetValue(exit.Id, out exec);
                }

                exec?.Finish(exit);
                return true;
            case ExecError error:
                lock (_gate)
                {
                    _execs.TryGetValue(error.Id, out exec);
                }

                exec?.Refuse(error);
                return true;
            case PutFileResult result:
                return CompletePut(result);
            case GetFileError error:
                return FailGet(error);
            case PutFile header:
                return await ReceiveFileAsync(header);
            case DeleteFileResult deleted:
                return CompleteRequest(PendingKind.Delete, deleted.Path, deleted, deleted.Ok ? null : deleted.Error ?? "the guest refused the delete");
            case ListFilesResult listed:
                if (listed.Error is null && (listed.Entries is null || listed.Entries.Any(entry => entry is null || !GuestPathRules.IsSafeEntryName(entry.Name))))
                {
                    End(GuestChannelState.Faulted, "a ListFilesResult carried a missing entry list or an entry name that is not a plain name.");
                    return false;
                }

                return CompleteRequest(PendingKind.List, listed.Dir, listed, listed.Error);
            case ResolveRealPathResult resolved:
                // Whatever the guest says, a real path that is not a safe root-relative path is treated as no path (fail closed).
                if (resolved.RealPath is not null && !GuestPathRules.IsSafeRelative(resolved.RealPath, allowRoot: true))
                {
                    resolved = resolved with { RealPath = null };
                }

                return CompleteRequest(PendingKind.Resolve, resolved.Path, resolved, null);
            case PtyData data:
                PtyState? pty;
                lock (_gate)
                {
                    _ptys.TryGetValue(data.Id, out pty);
                }

                if (data.Bytes is null)
                {
                    End(GuestChannelState.Faulted, "a PtyData frame carried no bytes.");
                    return false;
                }

                if (pty is not null && !pty.AddOutput(data.Bytes))
                {
                    // The reader fell too far behind. This terminal is ended and the guest told; the channel and other terminals carry on.
                    lock (_gate)
                    {
                        _ptys.Remove(data.Id);
                    }

                    pty.Complete(-1);
                    _ = TrySendPtyCloseAsync(data.Id);
                }

                return true;
            case PtyExit exited:
                lock (_gate)
                {
                    _ptys.Remove(exited.Id, out pty);
                }

                pty?.Complete(exited.Code);
                return true;
            default:
                End(GuestChannelState.Faulted, $"{message.GetType().Name} is not a frame a guest may send.");
                return false;
        }
    }

    private bool CompletePut(PutFileResult result)
    {
        var pending = CurrentFile(PendingKind.Put, result.Path);
        if (pending is null)
        {
            End(GuestChannelState.Faulted, "the guest answered a file write nobody asked for.");
            return false;
        }

        if (result.Ok)
        {
            pending.Done.TrySetResult();
        }
        else
        {
            pending.Done.TrySetException(new GuestFileException(result.Error ?? "the guest refused the file"));
        }

        FinishFile(pending);
        return true;
    }

    private bool FailGet(GetFileError error)
    {
        var pending = CurrentFile(PendingKind.Get, error.Path);
        if (pending is null)
        {
            End(GuestChannelState.Faulted, "the guest answered a file read nobody asked for.");
            return false;
        }

        pending.Done.TrySetException(new GuestFileException(error.Error));
        FinishFile(pending);
        return true;
    }

    /// <summary>Reads the bytes behind a guest PutFile header into the waiting GetFile's sink, hashing as they arrive.</summary>
    private async ValueTask<bool> ReceiveFileAsync(PutFile header)
    {
        var pending = CurrentFile(PendingKind.Get, header.Path);
        if (pending is null || header.Size < 0)
        {
            End(GuestChannelState.Faulted, "the guest sent file content nobody asked for.");
            return false;
        }

        if (header.Size > _options.MaxFileBytes)
        {
            // The size is the guest's claim. Reading "to stay in step" would let a hostile guest pin the reader for as long as it likes, so the
            // channel ends instead; the pending read fails with the limit, not with the generic channel error.
            End(
                GuestChannelState.Faulted,
                $"the guest announced {header.Size} bytes, over the {_options.MaxFileBytes}-byte file limit.",
                new GuestFileException($"The file is {header.Size} bytes, over the {_options.MaxFileBytes}-byte limit."));
            return false;
        }

        pending.Receiving = true;
        // The whole transfer has one deadline, so a guest that trickles bytes, each resetting the heartbeat watchdog, cannot hold the file gate.
        using var timeout = new CancellationTokenSource(_options.FileTransferTimeout, _time);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(_stop.Token, timeout.Token);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[_copyBytes];
        var remaining = header.Size;
        string? sinkError = null;
        while (remaining > 0)
        {
            var count = (int)Math.Min(buffer.Length, remaining);
            try
            {
                await _stream.ReadExactlyAsync(buffer.AsMemory(0, count), linked.Token);
            }
            catch (Exception ex) when (ex is EndOfStreamException or IOException or OperationCanceledException or ObjectDisposedException or SocketException)
            {
                End(GuestChannelState.Faulted, timeout.IsCancellationRequested
                    ? $"a file transfer from the guest took longer than {_options.FileTransferTimeout}"
                    : "the channel ended inside file content.");
                return false;
            }

            Volatile.Write(ref _seen, 1);
            remaining -= count;
            hash.AppendData(buffer, 0, count);

            // Keep reading after a sink failure or an abandoned call, so the stream stays in step. The size is within the limit here.
            if (sinkError is null && !pending.Abandoned)
            {
                try
                {
                    await pending.Sink!.WriteAsync(buffer.AsMemory(0, count), linked.Token);
                }
#pragma warning disable CA1031 // The sink is the caller's code. Whatever it throws must fail that call, not the reader, so the stream stays in step.
                catch (Exception ex) when (ex is not OperationCanceledException)
#pragma warning restore CA1031
                {
                    sinkError = ex.Message;
                }
                catch (OperationCanceledException) when (timeout.IsCancellationRequested)
                {
                    End(GuestChannelState.Faulted, $"a file transfer from the guest took longer than {_options.FileTransferTimeout}");
                    return false;
                }
            }
        }

        var actual = Convert.ToHexStringLower(hash.GetHashAndReset());
        if (!string.Equals(actual, header.Sha256, StringComparison.OrdinalIgnoreCase))
        {
            pending.Done.TrySetException(new GuestFileException("The received content does not match the guest's SHA-256. Discard what the sink received."));
        }
        else if (sinkError is not null)
        {
            pending.Done.TrySetException(new GuestFileException($"The sink failed: {sinkError}"));
        }
        else
        {
            pending.Done.TrySetResult();
        }

        FinishFile(pending);
        return true;
    }

    private PendingFile? CurrentFile(PendingKind kind, string path)
    {
        lock (_gate)
        {
            return _file is { } file && file.Kind == kind && string.Equals(file.Path, path, StringComparison.Ordinal) ? file : null;
        }
    }

    private void FinishFile(PendingFile pending)
    {
        lock (_gate)
        {
            if (ReferenceEquals(_file, pending))
            {
                _file = null;
            }
        }

        pending.Release();
    }

    private async Task WatchdogAsync(PeriodicTimer timer)
    {
        using var owned = timer;
        var missed = 0;
        try
        {
            while (await timer.WaitForNextTickAsync(_stop.Token))
            {
                missed = Interlocked.Exchange(ref _seen, 0) == 0 ? missed + 1 : 0;
                if (missed >= _options.MissedHeartbeatLimit)
                {
                    End(GuestChannelState.Lost, $"no frame from the guest for {missed} heartbeat intervals");
                    return;
                }

                // A heartbeat prompts the guest to echo one. It is skipped while a frame or a file is being written, since data is flowing then.
                if (_writeLock.Wait(0))
                {
                    try
                    {
                        await WriteFrameAsync(new Heartbeat());
                    }
                    catch (GuestChannelException)
                    {
                        return;
                    }
                    finally
                    {
                        _writeLock.Release();
                    }
                }
            }
        }
        catch (OperationCanceledException)
        {
            // The channel ended.
        }
    }

    /// <summary>Ends the channel once. The first caller's state and reason stand. Fails everything in flight; a pending file call fails with <paramref name="fileFailure"/> when given.</summary>
    private void End(GuestChannelState state, string reason, Exception? fileFailure = null)
    {
        ExecState[] execs;
        PtyState[] ptys;
        PendingFile? file;
        lock (_gate)
        {
            if (State != GuestChannelState.Open)
            {
                return;
            }

            Volatile.Write(ref _state, (int)state);
            _reason = reason;
            execs = [.. _execs.Values];
            ptys = [.. _ptys.Values];
            _ptys.Clear();
            file = _file;
        }

        _stop.Cancel();
        var failure = new GuestChannelException($"The guest channel ended ({state}): {reason}.");
        foreach (var exec in execs)
        {
            exec.Fail(failure);
        }

        foreach (var pty in ptys)
        {
            pty.Complete(-1);
        }

        if (file is not null)
        {
            file.Done.TrySetException(fileFailure ?? failure);
            FinishFile(file);
        }

        // Closing the stream wakes a reader blocked on it, and tells the guest the host is gone.
        _connection.Stream.Dispose();
        _completion.TrySetResult();
    }

    private void ThrowIfEnded()
    {
        if (State != GuestChannelState.Open)
        {
            throw EndedException();
        }
    }

    private GuestChannelException EndedException() => new($"The guest channel is {State}: {EndReason}.");

    private enum PendingKind
    {
        Put,
        Get,
        Delete,
        List,
        Resolve,
    }

    /// <summary>One file transfer in flight. Only one runs at a time, because the guest answers by path and sends content inline.</summary>
    private sealed class PendingFile
    {
        private int _released;
        private int _abandoned;

        private PendingFile(PendingKind kind, string path, Stream? sink)
        {
            Kind = kind;
            Path = path;
            Sink = sink;
        }

        public PendingKind Kind { get; }

        public string Path { get; }

        public Stream? Sink { get; }

        public SemaphoreSlim? Gate { get; set; }

        public TaskCompletionSource Done { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public bool Abandoned => Volatile.Read(ref _abandoned) == 1;

        /// <summary>True once the guest's content for a read has started to arrive.</summary>
        public bool Receiving { get; set; }

        /// <summary>The guest's answer to a Delete, List or Resolve request, set before <see cref="Done"/> completes.</summary>
        public GuestMessage? Reply { get; set; }

        public static PendingFile Request(PendingKind kind, string path) => new(kind, path, null);

        public static PendingFile Put(string path) => new(PendingKind.Put, path, null);

        public static PendingFile Get(string path, Stream sink) => new(PendingKind.Get, path, sink);

        public void Abandon() => Volatile.Write(ref _abandoned, 1);

        public void Release()
        {
            if (Interlocked.Exchange(ref _released, 1) == 0)
            {
                Gate?.Release();
            }
        }
    }

    /// <summary>One running exec: its output pipeline and its result.</summary>
    private sealed class ExecState
    {
        private readonly long _cap;
        private readonly Decoder _stdout = Encoding.UTF8.GetDecoder();
        private readonly Decoder _stderr = Encoding.UTF8.GetDecoder();
        private long _bytes;
        private long _seq;
        private int _truncated;

        public ExecState(string id, long cap)
        {
            Id = id;
            _cap = cap;
        }

        public string Id { get; }

        public Channel<OutputChunk> Chunks { get; } = Channel.CreateUnbounded<OutputChunk>(new UnboundedChannelOptions { SingleReader = true, SingleWriter = true });

        public TaskCompletionSource<Exit> Exited { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public bool Truncated => Volatile.Read(ref _truncated) == 1;

        public System.Runtime.ExceptionServices.ExceptionDispatchInfo? CallbackFailure { get; private set; }

        /// <summary>Runs the caller's callback over the chunks in order. A callback that throws cancels the exec and the rest is discarded.</summary>
        public async Task ConsumeAsync(Func<OutputChunk, Task> onOutput, Func<Task> cancel)
        {
            await foreach (var chunk in Chunks.Reader.ReadAllAsync())
            {
                if (CallbackFailure is not null)
                {
                    continue;
                }

                try
                {
                    await onOutput(chunk);
                }
                catch (Exception ex)
                {
                    // The caller's code failed, so its exec is stopped and its error is rethrown from ExecAsync.
                    CallbackFailure = System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(ex);
                    await cancel();
                }
            }
        }

        public void AddOutput(Output output)
        {
            var room = _cap - _bytes;
            var take = (int)Math.Min(output.Bytes.Length, Math.Max(room, 0));
            if (take < output.Bytes.Length)
            {
                Volatile.Write(ref _truncated, 1);
            }

            if (take == 0)
            {
                return;
            }

            _bytes += take;
            var decoder = output.Stream == OutputSource.Stderr ? _stderr : _stdout;
            var text = new string(DecodeChars(decoder, output.Bytes.AsSpan(0, take)));
            if (text.Length > 0)
            {
                Chunks.Writer.TryWrite(new OutputChunk(_seq++, output.Stream == OutputSource.Stderr ? "stderr" : "stdout", text));
            }
        }

        public void Finish(Exit exit)
        {
            Chunks.Writer.TryComplete();
            Exited.TrySetResult(exit);
        }

        public void Refuse(ExecError error)
        {
            Chunks.Writer.TryComplete();
            Exited.TrySetException(new GuestExecRefusedException(error.Code, error.Message));
        }

        public void Fail(Exception failure)
        {
            Chunks.Writer.TryComplete();
            Exited.TrySetException(failure);
        }

        private static char[] DecodeChars(Decoder decoder, ReadOnlySpan<byte> bytes)
        {
            var chars = new char[decoder.GetCharCount(bytes, flush: false)];
            var written = decoder.GetChars(bytes, chars, flush: false);
            return chars[..written];
        }
    }

    /// <summary>One terminal.</summary>
    private sealed class PtyState : IGuestPty
    {
        private readonly GuestChannel _owner;
        private readonly Channel<byte[]> _channel = Channel.CreateUnbounded<byte[]>(new UnboundedChannelOptions { SingleReader = true, SingleWriter = true });
        private readonly CountingReader _reader;
        private readonly TaskCompletionSource<int> _exited = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly long _maxPendingBytes;
        private long _seq;
        private long _pendingBytes;
        private int _closed;

        public PtyState(GuestChannel owner, string id, long maxPendingBytes)
        {
            _owner = owner;
            Id = id;
            _maxPendingBytes = maxPendingBytes;
            _reader = new CountingReader(_channel.Reader, this);
        }

        public string Id { get; }

        public ChannelReader<byte[]> Output => _reader;

        public Task<int> Exited => _exited.Task;

        /// <summary>Queues output. False when the reader is further behind than the byte limit, which ends the terminal.</summary>
        public bool AddOutput(byte[] bytes)
        {
            if (Interlocked.Add(ref _pendingBytes, bytes.Length) > _maxPendingBytes)
            {
                return false;
            }

            _channel.Writer.TryWrite(bytes);
            return true;
        }

        public void Complete(int code)
        {
            _channel.Writer.TryComplete();
            _exited.TrySetResult(code);
        }

        public async ValueTask WriteAsync(ReadOnlyMemory<byte> bytes, CancellationToken ct)
        {
            for (var offset = 0; offset < bytes.Length; offset += _ptyChunkBytes)
            {
                var chunk = bytes.Slice(offset, Math.Min(_ptyChunkBytes, bytes.Length - offset)).ToArray();

                // The sequence number is taken under the write lock, so frames carry numbers in the order they reach the wire.
                await _owner.SendAsync(() => new PtyData(Id, _seq++, chunk), ct);
            }
        }

        public ValueTask ResizeAsync(int cols, int rows, CancellationToken ct) => _owner.SendAsync(new PtyResize(Id, cols, rows), ct);

        public async ValueTask CloseAsync()
        {
            if (Interlocked.Exchange(ref _closed, 1) == 1)
            {
                return;
            }

            lock (_owner._gate)
            {
                _owner._ptys.Remove(Id);
            }

            // Only a terminal that was still open needs a PtyClose. One whose shell already exited, or whose channel ended, is gone.
            var wasOpen = !_exited.Task.IsCompleted;
            Complete(-1);
            if (wasOpen)
            {
                await _owner.TrySendPtyCloseAsync(Id);
            }
        }

        public ValueTask DisposeAsync() => CloseAsync();

        private void Consumed(int length) => Interlocked.Add(ref _pendingBytes, -length);

        /// <summary>Wraps the queue so the pending byte count falls as the reader takes chunks.</summary>
        private sealed class CountingReader : ChannelReader<byte[]>
        {
            private readonly ChannelReader<byte[]> _inner;
            private readonly PtyState _owner;

            public CountingReader(ChannelReader<byte[]> inner, PtyState owner)
            {
                _inner = inner;
                _owner = owner;
            }

            public override Task Completion => _inner.Completion;

            public override bool CanCount => false;

            public override bool TryRead(out byte[] item)
            {
                if (_inner.TryRead(out var read))
                {
                    _owner.Consumed(read.Length);
                    item = read;
                    return true;
                }

                item = [];
                return false;
            }

            public override ValueTask<bool> WaitToReadAsync(CancellationToken cancellationToken = default) => _inner.WaitToReadAsync(cancellationToken);
        }
    }
}
