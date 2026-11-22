using Bazaar.Server.BrowserPool.Abstractions;

namespace Bazaar.Server.BrowserPool.Display;

/// <summary>
/// One DevTools connection over a pool session's pipe pair. A single reader loop reads every message; a reply goes to the call
/// waiting for its id and anything else (events, replies nobody waits for) is dropped at once, so nothing the browser sends can
/// grow memory. Calls have a timeout, the number of calls waiting is bounded, and when the pipe ends or its framing fails every
/// waiting call fails and the session stays closed. The class never takes ownership of the pipe's streams.
/// It takes a <see cref="CdpMethod"/>, never a method name, so only the allow-listed methods can be sent.
/// </summary>
internal sealed class DevToolsSession : IAsyncDisposable
{
    private readonly DevToolsMessageWriter _writer;
    private readonly DevToolsMessageReader _reader;
    private readonly BrowserPageDriverOptions _options;
    private readonly Lock _lock = new();
    private readonly Dictionary<long, TaskCompletionSource<byte[]>> _pending = [];
    private readonly CancellationTokenSource _stop = new();
    private readonly Task _loop;
    private long _nextId;
    private long _dropped;
    private int _disposed;
    private PageDriverException? _closed;

    /// <summary>Starts the reader loop over the pipe.</summary>
    public DevToolsSession(PoolDevToolsPipe pipe, BrowserPageDriverOptions options)
    {
        ArgumentNullException.ThrowIfNull(pipe);
        ArgumentNullException.ThrowIfNull(options);
        options.Validate();
        _options = options;
        Pipe = pipe;

        // A command is small; a reply may carry a whole screenshot, so only the reader's limit follows MaxScreenshotBytes.
        _writer = new DevToolsMessageWriter(pipe.ToBrowser, 4 * 1024 * 1024);
        _reader = new DevToolsMessageReader(pipe.FromBrowser, options.MaxMessageBytes);
        _loop = Task.Run(ReadLoopAsync);
    }

    /// <summary>The pipe this session owns the use of.</summary>
    public PoolDevToolsPipe Pipe { get; }

    /// <summary>Whether the session has failed or been disposed. It never reopens.</summary>
    public bool IsClosed
    {
        get
        {
            lock (_lock)
            {
                return _closed is not null;
            }
        }
    }

    /// <summary>How many calls wait for a reply now.</summary>
    public int PendingCount
    {
        get
        {
            lock (_lock)
            {
                return _pending.Count;
            }
        }
    }

    /// <summary>How many messages were dropped (events, malformed messages, replies nobody waited for).</summary>
    public long DroppedMessages => Interlocked.Read(ref _dropped);

    /// <summary>
    /// Sends one command and returns the whole reply message. <paramref name="cdpSessionId"/> is the flattened target session id
    /// for page commands, null for browser-level ones.
    /// </summary>
    /// <exception cref="PageDriverException">Closed, busy, or the browser answered with an error.</exception>
    /// <exception cref="TimeoutException">No reply within the command timeout.</exception>
    /// <exception cref="OperationCanceledException">The caller cancelled.</exception>
    public async Task<byte[]> SendAsync(CdpMethod method, string? cdpSessionId, ReadOnlyMemory<byte> paramsJson, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        long id;
        TaskCompletionSource<byte[]> reply;
        lock (_lock)
        {
            if (_closed is not null)
            {
                throw Closed(_closed);
            }

            if (_pending.Count >= _options.MaxPendingRequests)
            {
                throw new PageDriverException(PageDriverFailure.Busy, "Too many DevTools commands are waiting for a reply.");
            }

            id = ++_nextId;
            reply = new TaskCompletionSource<byte[]>(TaskCreationOptions.RunContinuationsAsynchronously);
            _pending.Add(id, reply);
        }

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(_options.CommandTimeout);
        try
        {
            var request = CdpJson.BuildRequest(id, method, cdpSessionId, paramsJson.Span);
            try
            {
                await _writer.WriteAsync(request, timeout.Token).ConfigureAwait(false);
            }
            catch (Exception e) when (e is IOException or ObjectDisposedException or NotSupportedException or OperationCanceledException)
            {
                // A write that failed or was cut short may have left half a message in the pipe, so the framing cannot be trusted.
                var failure = new PageDriverException(PageDriverFailure.PipeClosed, "Writing to the browser's DevTools pipe failed.", e);
                Fail(failure);
                if (e is OperationCanceledException && ct.IsCancellationRequested)
                {
                    throw;
                }

                throw failure;
            }

            return await reply.Task.WaitAsync(timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new TimeoutException("A DevTools command did not get a reply in time.");
        }
        finally
        {
            lock (_lock)
            {
                _pending.Remove(id);
            }
        }
    }

    /// <summary>Fails every waiting call, stops the loop and releases the write lock. The pipe's streams are left to the host.</summary>
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        Fail(new PageDriverException(PageDriverFailure.PipeClosed, "The DevTools session was closed."));
        await _stop.CancelAsync().ConfigureAwait(false);
        try
        {
            // A read on a FIFO may not notice the cancellation; the host closes the stream when the browser ends.
            await _loop.WaitAsync(TimeSpan.FromSeconds(1)).ConfigureAwait(false);
            _stop.Dispose();
        }
        catch (TimeoutException)
        {
        }

        _writer.Dispose();
    }

    private static PageDriverException Closed(PageDriverException reason) => new(PageDriverFailure.PipeClosed, reason.Message, reason);

    private async Task ReadLoopAsync()
    {
        try
        {
            while (true)
            {
                var message = await _reader.ReadAsync(_stop.Token).ConfigureAwait(false);
                if (message is null)
                {
                    Fail(new PageDriverException(PageDriverFailure.PipeClosed, "The browser's DevTools pipe ended."));
                    return;
                }

                Route(message);
            }
        }
        catch (OperationCanceledException) when (_stop.IsCancellationRequested)
        {
            Fail(new PageDriverException(PageDriverFailure.PipeClosed, "The DevTools session was closed."));
        }
        catch (Exception e) when (e is IOException or ObjectDisposedException or NotSupportedException or InvalidOperationException)
        {
            // DevToolsFramingException is an IOException: an oversized or cut-off message ends the session.
            Fail(new PageDriverException(PageDriverFailure.PipeClosed, "Reading from the browser's DevTools pipe failed.", e));
        }
    }

    private void Route(byte[] message)
    {
        if (!CdpJson.TryScanEnvelope(message, out var envelope) || !envelope.HasId)
        {
            Interlocked.Increment(ref _dropped);
            return;
        }

        TaskCompletionSource<byte[]>? waiting;
        lock (_lock)
        {
            _pending.TryGetValue(envelope.Id, out waiting);
        }

        if (waiting is null)
        {
            Interlocked.Increment(ref _dropped);
            return;
        }

        if (envelope.IsError)
        {
            // Only the numeric code is kept: the browser's text could echo page content.
            waiting.TrySetException(new PageDriverException(PageDriverFailure.ProtocolError, $"The browser refused a DevTools command (code {envelope.ErrorCode}).") { ProtocolCode = envelope.ErrorCode });
            return;
        }

        waiting.TrySetResult(message);
    }

    private void Fail(PageDriverException reason)
    {
        List<TaskCompletionSource<byte[]>> waiting;
        lock (_lock)
        {
            if (_closed is not null)
            {
                return;
            }

            _closed = reason;
            waiting = [.. _pending.Values];
            _pending.Clear();
        }

        foreach (var w in waiting)
        {
            w.TrySetException(Closed(reason));
        }
    }
}
