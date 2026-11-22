using System.Text.Json;
using System.Threading.Channels;
using Bazaar.Contracts.Terminal;
using Bazaar.Server.Terminal.Abstractions;

namespace Bazaar.Server.Terminal.Stream;

/// <summary>
/// The transport-agnostic terminal layer of one authenticated client connection (doc 55, sections 4 and 6). The wire host feeds
/// inbound <c>pane</c> frames to <see cref="HandleInboundAsync"/> and writes <see cref="Outbound"/> to the socket.
/// The caller identity is fixed at construction and never read from a frame; every decision is the broker's, so a spoofed
/// terminal id is a refusal. Fails closed: a broker refusal or fault becomes an error frame, never an exception. Thread safe.
/// </summary>
public sealed class TerminalConnection : IAsyncDisposable
{
    /// <summary>Terminal id used on connection-level error frames that name no valid terminal.</summary>
    public const string ConnectionScopeId = "connection";

    /// <summary>Error code: the inbound frame was not valid JSON or not an object.</summary>
    public const string CodeMalformed = "terminal.malformed";

    /// <summary>Error code: the inbound frame was well formed but not acceptable (unknown type, missing or invalid field, wrong direction).</summary>
    public const string CodeBadRequest = "terminal.bad_request";

    /// <summary>Error code: the inbound frame or its payload was too large.</summary>
    public const string CodeOversize = "terminal.oversize";

    /// <summary>Error code sent just before the connection closes for too many consecutive malformed frames.</summary>
    public const string CodeTooManyMalformed = "terminal.too_many_malformed";

    /// <summary>Error code: the connection is already subscribed to its maximum number of terminals.</summary>
    public const string CodeSubscriptionLimit = "terminal.subscription_limit";

    /// <summary>Error code: an unexpected fault; no detail is leaked.</summary>
    public const string CodeInternal = "terminal.error";

    private readonly ITerminalBroker _broker;
    private readonly TerminalCaller _caller;
    private readonly ITerminalTargetResolver _resolver;
    private readonly TerminalConnectionOptions _options;
    private readonly OutboundQueue _queue;
    private readonly object _gate = new();
    private readonly Dictionary<string, Pump> _pumps = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim _subscribeLock = new(1, 1);
    private readonly TaskCompletionSource _closed = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int _malformedRun;
    private int _closing;
    private int _disposed;

    /// <summary>Creates the layer for one connection. The options are normalised.</summary>
    /// <param name="broker">The terminal broker.</param>
    /// <param name="caller">The authenticated caller, taken from the connection and never from a frame.</param>
    /// <param name="resolver">Locates the VM for a new terminal.</param>
    /// <param name="options">Optional settings.</param>
    public TerminalConnection(ITerminalBroker broker, TerminalCaller caller, ITerminalTargetResolver resolver, TerminalConnectionOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(broker);
        ArgumentNullException.ThrowIfNull(caller);
        ArgumentNullException.ThrowIfNull(resolver);
        _broker = broker;
        _caller = caller;
        _resolver = resolver;
        _options = (options ?? new TerminalConnectionOptions()).Normalize();
        _queue = new OutboundQueue(_options.OutboundQueueFrames);
    }

    /// <summary>Encoded frames for the wire host to send, in order. Completes after the connection closes and the rest is drained.</summary>
    public ChannelReader<ReadOnlyMemory<byte>> Outbound => _queue;

    /// <summary>Completes when the connection closed itself (malformed budget, queue overflow) or was disposed.</summary>
    public Task Closed => _closed.Task;

    /// <summary>True once the connection no longer accepts frames.</summary>
    public bool IsClosed => Volatile.Read(ref _closing) != 0;

    /// <summary>Output frames dropped because the outbound queue was full.</summary>
    public long DroppedOutputFrames => _queue.DroppedOutputFrames;

    /// <summary>Number of terminals this connection is currently subscribed to.</summary>
    public int SubscriptionCount
    {
        get
        {
            lock (_gate)
            {
                return _pumps.Count;
            }
        }
    }

    /// <summary>The terminals this caller may see, for the wire host to publish on the bare <c>pane</c> stream. Empty when the broker faults.</summary>
    public IReadOnlyList<TerminalInfo> VisibleTerminals()
    {
        try
        {
            return _broker.List(_caller);
        }
        catch (Exception)
        {
            return [];
        }
    }

    /// <summary>
    /// Handles one inbound frame. Malformed or refused input becomes an outbound error frame, never an exception; only cancellation
    /// of <paramref name="ct"/> propagates. May be called concurrently.
    /// </summary>
    public async ValueTask HandleInboundAsync(ReadOnlyMemory<byte> json, CancellationToken ct)
    {
        if (IsClosed)
        {
            return;
        }

        ct.ThrowIfCancellationRequested();
        TerminalFrame frame;
        if (json.Length > _options.MaxInboundFrameBytes)
        {
            await RejectAsync(CodeOversize, "the frame is too large", TerminalFrameFault.Oversize, ReadOnlyMemory<byte>.Empty).ConfigureAwait(false);
            return;
        }

        var decoded = TerminalFrameCodec.Decode(json.Span);
        if (decoded.Frame is null)
        {
            await RejectAsync(CodeFor(decoded.Fault), decoded.Detail ?? "invalid frame", decoded.Fault, json).ConfigureAwait(false);
            return;
        }

        frame = decoded.Frame;
        Interlocked.Exchange(ref _malformedRun, 0);
        try
        {
            await DispatchAsync(frame, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception)
        {
            Send(new TerminalError(frame.TerminalId, CodeInternal, "the terminal operation failed"));
        }
    }

    /// <summary>Detaches every subscription, ends every pump, completes <see cref="Outbound"/> and <see cref="Closed"/>. Idempotent.</summary>
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            await _closed.Task.ConfigureAwait(false);
            return;
        }

        Interlocked.Exchange(ref _closing, 1);
        await TearDownAsync().ConfigureAwait(false);
    }

    private static string CodeFor(TerminalFrameFault fault) => fault switch
    {
        TerminalFrameFault.Malformed => CodeMalformed,
        TerminalFrameFault.Oversize => CodeOversize,
        _ => CodeBadRequest,
    };

    private static string ScopeFor(ReadOnlyMemory<byte> json)
    {
        if (json.IsEmpty)
        {
            return ConnectionScopeId;
        }

        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind == JsonValueKind.Object
                && doc.RootElement.TryGetProperty("terminalId", out var id)
                && id.ValueKind == JsonValueKind.String
                && Bazaar.Contracts.Wire.WireStreams.IsValidTerminalId(id.GetString()))
            {
                return id.GetString()!;
            }
        }
        catch (JsonException)
        {
        }

        return ConnectionScopeId;
    }

    private async ValueTask RejectAsync(string code, string detail, TerminalFrameFault fault, ReadOnlyMemory<byte> json)
    {
        _ = fault;
        Send(new TerminalError(ScopeFor(json), code, detail));
        if (Interlocked.Increment(ref _malformedRun) >= _options.MaxConsecutiveMalformed
            && Interlocked.Exchange(ref _closing, 1) == 0)
        {
            Send(new TerminalError(ConnectionScopeId, CodeTooManyMalformed, "too many malformed frames; closing"));
            await TearDownAsync().ConfigureAwait(false);
        }
    }

    private async ValueTask DispatchAsync(TerminalFrame frame, CancellationToken ct)
    {
        switch (frame)
        {
            case TerminalOpen open:
                await OpenAsync(open, ct).ConfigureAwait(false);
                break;
            case TerminalReplay replay:
                await ResubscribeAsync(replay.TerminalId, Math.Max(0, replay.FromSeq), ct).ConfigureAwait(false);
                break;
            case TerminalClose close:
                await CloseTerminalAsync(close, ct).ConfigureAwait(false);
                break;
            case TerminalControl control:
                await ControlAsync(control, ct).ConfigureAwait(false);
                break;
            case TerminalInput input:
                SendIfRefused(input.TerminalId, await _broker.WriteAsync(_caller, input.TerminalId, input.Bytes ?? [], ct).ConfigureAwait(false));
                break;
            case TerminalResize resize:
                SendIfRefused(resize.TerminalId, await _broker.ResizeAsync(_caller, resize.TerminalId, resize.Cols, resize.Rows, ct).ConfigureAwait(false));
                break;
            default:
                // Opened, Output, Exit and Error only travel from server to client.
                Send(new TerminalError(frame.TerminalId, CodeBadRequest, "this frame is not accepted from a client"));
                break;
        }
    }

    private async ValueTask OpenAsync(TerminalOpen open, CancellationToken ct)
    {
        await _subscribeLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (IsClosed)
            {
                return;
            }

            // Attach when the id names a terminal this caller may see; the broker (not this layer) decides visibility.
            var (attach, existing) = await _broker.SubscribeAsync(_caller, open.TerminalId, 0, ct).ConfigureAwait(false);
            if (attach.Ok && existing is not null)
            {
                await StartPumpAsync(open.TerminalId, existing, ct).ConfigureAwait(false);
                return;
            }

            if (attach.Refusal != TerminalRefusal.NotFound)
            {
                Send(TerminalFrameMapper.ToError(open.TerminalId, attach));
                return;
            }

            if (SubscriptionCount >= _options.MaxSubscriptions)
            {
                Send(new TerminalError(open.TerminalId, CodeSubscriptionLimit, "too many terminals on this connection"));
                return;
            }

            var target = await _resolver.ResolveAsync(_caller, open.AgentId, ct).ConfigureAwait(false);
            if (target is null)
            {
                Send(TerminalFrameMapper.ToError(open.TerminalId, new TerminalResult(TerminalRefusal.NotFound, "no such agent or VM")));
                return;
            }

            var (opened, info) = await _broker.OpenAsync(_caller, target.AgentId, target.VmId, open.Cols, open.Rows, ct).ConfigureAwait(false);
            if (!opened.Ok || info is null)
            {
                Send(TerminalFrameMapper.ToError(open.TerminalId, opened.Ok ? new TerminalResult(TerminalRefusal.NotFound) : opened));
                return;
            }

            var (sub, subscription) = await _broker.SubscribeAsync(_caller, info.TerminalId, 0, ct).ConfigureAwait(false);
            if (!sub.Ok || subscription is null)
            {
                // The caller may open but not view: the terminal stays (idle close reaps it); report why this connection has no stream.
                Send(TerminalFrameMapper.ToError(info.TerminalId, sub.Ok ? new TerminalResult(TerminalRefusal.NotFound) : sub));
                return;
            }

            await StartPumpAsync(info.TerminalId, subscription, ct).ConfigureAwait(false);
        }
        finally
        {
            _subscribeLock.Release();
        }
    }

    private async ValueTask ResubscribeAsync(string terminalId, long fromSeq, CancellationToken ct)
    {
        await _subscribeLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (IsClosed)
            {
                return;
            }

            bool already;
            lock (_gate)
            {
                already = _pumps.ContainsKey(terminalId);
            }

            if (!already && SubscriptionCount >= _options.MaxSubscriptions)
            {
                Send(new TerminalError(terminalId, CodeSubscriptionLimit, "too many terminals on this connection"));
                return;
            }

            var (result, subscription) = await _broker.SubscribeAsync(_caller, terminalId, fromSeq, ct).ConfigureAwait(false);
            if (!result.Ok || subscription is null)
            {
                Send(TerminalFrameMapper.ToError(terminalId, result.Ok ? new TerminalResult(TerminalRefusal.NotFound) : result));
                return;
            }

            await StartPumpAsync(terminalId, subscription, ct, announce: false).ConfigureAwait(false);
        }
        finally
        {
            _subscribeLock.Release();
        }
    }

    private async ValueTask CloseTerminalAsync(TerminalClose close, CancellationToken ct)
    {
        var result = await _broker.CloseAsync(_caller, close.TerminalId, ct).ConfigureAwait(false);
        if (result.Ok)
        {
            // The pump delivers the exit frame once the session ends.
            return;
        }

        // A viewer who may not close the session still detaches. Anything else is reported.
        if (result.Refusal == TerminalRefusal.Forbidden && await DetachAsync(close.TerminalId).ConfigureAwait(false))
        {
            return;
        }

        Send(TerminalFrameMapper.ToError(close.TerminalId, result));
    }

    private async ValueTask ControlAsync(TerminalControl control, CancellationToken ct)
    {
        TerminalResult result;
        if (string.Equals(control.Action, TerminalFrameMapper.ActionTake, StringComparison.Ordinal))
        {
            result = await _broker.TakeControlAsync(_caller, control.TerminalId, ct).ConfigureAwait(false);
        }
        else if (string.Equals(control.Action, TerminalFrameMapper.ActionRelease, StringComparison.Ordinal))
        {
            result = await _broker.ReleaseControlAsync(_caller, control.TerminalId, ct).ConfigureAwait(false);
        }
        else
        {
            Send(new TerminalError(control.TerminalId, CodeBadRequest, "unknown control action"));
            return;
        }

        if (result.Ok)
        {
            Send(TerminalFrameMapper.ToControlReply(control.TerminalId, granted: true));
        }
        else if (result.Refusal == TerminalRefusal.WriterHeld)
        {
            Send(TerminalFrameMapper.ToControlReply(control.TerminalId, granted: false));
        }
        else
        {
            Send(TerminalFrameMapper.ToError(control.TerminalId, result));
        }
    }

    private async ValueTask StartPumpAsync(string terminalId, ITerminalSubscription subscription, CancellationToken ct, bool announce = true)
    {
        // Replay chunks are queued synchronously by the broker before Subscribe returns, so the first chunk tells the replay origin.
        TerminalOutputChunk? first = null;
        if (subscription.Output.TryRead(out var chunk))
        {
            first = chunk;
        }

        Pump? pump = new Pump(terminalId, subscription);
        Pump? previous;
        lock (_gate)
        {
            if (IsClosed)
            {
                previous = null;
                pump.Dispose();
                pump = null;
            }
            else
            {
                _pumps.TryGetValue(terminalId, out previous);
                _pumps[terminalId] = pump;
            }
        }

        if (pump is null)
        {
            await subscription.DisposeAsync().ConfigureAwait(false);
            return;
        }

        if (previous is not null)
        {
            await previous.StopAsync().ConfigureAwait(false);
        }

        if (announce)
        {
            var from = first is { } f ? f.Seq : LastSeqOf(terminalId) + 1;
            Send(new TerminalOpened(terminalId, WriterHeld(terminalId), Math.Max(1, from)));
        }

        pump.Start(Task.Run(() => RunPumpAsync(pump, first), CancellationToken.None));
        ct.ThrowIfCancellationRequested();
    }

    private long LastSeqOf(string terminalId)
    {
        foreach (var info in VisibleTerminals())
        {
            if (string.Equals(info.TerminalId, terminalId, StringComparison.Ordinal))
            {
                return info.LastSeq;
            }
        }

        return 0;
    }

    private bool WriterHeld(string terminalId)
    {
        foreach (var info in VisibleTerminals())
        {
            if (string.Equals(info.TerminalId, terminalId, StringComparison.Ordinal))
            {
                return info.WriterMemberId is not null;
            }
        }

        return false;
    }

    private async Task RunPumpAsync(Pump pump, TerminalOutputChunk? first)
    {
        var token = pump.Token;
        try
        {
            if (first is { } head && !SendOutput(pump.TerminalId, head))
            {
                return;
            }

            var reader = pump.Subscription.Output;
            while (await reader.WaitToReadAsync(token).ConfigureAwait(false))
            {
                while (reader.TryRead(out var chunk))
                {
                    if (!SendOutput(pump.TerminalId, chunk))
                    {
                        return;
                    }
                }
            }

            // The output ended: the shell exited (a detach by this connection cancels the token instead).
            var code = await pump.Subscription.Exited.WaitAsync(token).ConfigureAwait(false);
            Send(TerminalFrameMapper.ToExit(pump.TerminalId, code));
        }
        catch (OperationCanceledException)
        {
            return;
        }
        catch (Exception)
        {
            Send(new TerminalError(pump.TerminalId, CodeInternal, "the terminal stream failed"));
        }
        finally
        {
            lock (_gate)
            {
                if (_pumps.TryGetValue(pump.TerminalId, out var current) && ReferenceEquals(current, pump))
                {
                    _pumps.Remove(pump.TerminalId);
                }
            }

            try
            {
                await pump.Subscription.DisposeAsync().ConfigureAwait(false);
            }
            catch (Exception)
            {
                // Detaching a subscription that is already gone is not an error.
            }
        }
    }

    private bool SendOutput(string terminalId, TerminalOutputChunk chunk)
    {
        var frame = TerminalFrameMapper.ToOutput(terminalId, chunk);
        var encoded = TerminalFrameCodec.Encode(frame);
        if (encoded.Bytes is null)
        {
            Send(new TerminalError(terminalId, CodeOversize, "an output frame was too large to send"));
            return false;
        }

        return Enqueue(encoded.Bytes, isOutput: true);
    }

    private void SendIfRefused(string terminalId, TerminalResult result)
    {
        if (!result.Ok)
        {
            Send(TerminalFrameMapper.ToError(terminalId, result));
        }
    }

    private void Send(TerminalFrame frame)
    {
        var encoded = TerminalFrameCodec.Encode(frame);
        if (encoded.Bytes is not null)
        {
            Enqueue(encoded.Bytes, isOutput: false);
        }
    }

    private bool Enqueue(byte[] bytes, bool isOutput)
    {
        var outcome = _queue.Enqueue(bytes, isOutput);
        if (outcome == EnqueueOutcome.Overflow && Interlocked.Exchange(ref _closing, 1) == 0)
        {
            // A peer that never reads while sending control traffic is cut off instead of growing the queue without bound.
            _ = Task.Run(TearDownAsync);
        }

        return outcome != EnqueueOutcome.Closed && outcome != EnqueueOutcome.Overflow;
    }

    private async ValueTask<bool> DetachAsync(string terminalId)
    {
        Pump? pump;
        lock (_gate)
        {
            if (!_pumps.Remove(terminalId, out pump))
            {
                return false;
            }
        }

        await pump.StopAsync().ConfigureAwait(false);
        return true;
    }

    private async Task TearDownAsync()
    {
        Pump[] pumps;
        lock (_gate)
        {
            pumps = [.. _pumps.Values];
            _pumps.Clear();
        }

        foreach (var pump in pumps)
        {
            await pump.StopAsync().ConfigureAwait(false);
        }

        _queue.Complete();
        _closed.TrySetResult();
    }

    private sealed class Pump : IDisposable
    {
        private readonly CancellationTokenSource _cts = new();
        private Task _task = Task.CompletedTask;

        public Pump(string terminalId, ITerminalSubscription subscription)
        {
            TerminalId = terminalId;
            Subscription = subscription;
        }

        public string TerminalId { get; }

        public ITerminalSubscription Subscription { get; }

        public CancellationToken Token => _cts.Token;

        public void Start(Task task) => _task = task;

        public async Task StopAsync()
        {
            try
            {
                await _cts.CancelAsync().ConfigureAwait(false);
            }
            catch (ObjectDisposedException)
            {
            }

            try
            {
                await Subscription.DisposeAsync().ConfigureAwait(false);
            }
            catch (Exception)
            {
            }

            try
            {
                await _task.ConfigureAwait(false);
            }
            catch (Exception)
            {
            }

            Dispose();
        }

        public void Dispose() => _cts.Dispose();
    }
}
