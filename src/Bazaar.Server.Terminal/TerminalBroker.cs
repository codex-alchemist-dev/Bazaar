using System.Collections.Concurrent;
using Bazaar.Server.Terminal.Abstractions;

namespace Bazaar.Server.Terminal;

/// <summary>
/// Creates, tracks and fans out agent terminals (doc 52, section 4). Fails closed: no VM backend, an unreadable policy or a
/// human-involved action whose audit row cannot be written is refused. Thread safe.
/// </summary>
public sealed class TerminalBroker : ITerminalBroker, IAsyncDisposable
{
    private static readonly TerminalCaller SystemCaller = new("system:terminal", IsOwner: false, IsAgent: false);

    private readonly ConcurrentDictionary<string, TerminalSession> _sessions = new(StringComparer.Ordinal);
    private readonly object _registry = new();
    private readonly IGuestPtyChannel _channel;
    private readonly ITerminalAccessPolicy _policy;
    private readonly ITerminalAudit _audit;
    private readonly TerminalOptions _options;
    private readonly TimeProvider _time;
    private readonly ITimer _sweeper;
    private int _disposed;

    /// <summary>Creates a broker. The options are normalised, so a setting can never go below its floor.</summary>
    public TerminalBroker(
        IGuestPtyChannel channel,
        ITerminalAccessPolicy policy,
        ITerminalAudit audit,
        TerminalOptions? options = null,
        TimeProvider? time = null)
    {
        ArgumentNullException.ThrowIfNull(channel);
        ArgumentNullException.ThrowIfNull(policy);
        ArgumentNullException.ThrowIfNull(audit);
        _channel = channel;
        _policy = policy;
        _audit = audit;
        _options = (options ?? new TerminalOptions()).Normalize();
        _time = time ?? TimeProvider.System;
        _sweeper = _time.CreateTimer(
            _ => _ = SweepAsync(),
            null,
            _options.SweepInterval,
            _options.SweepInterval);
    }

    /// <summary>The effective (normalised) options.</summary>
    public TerminalOptions Options => _options;

    /// <inheritdoc />
    public async Task<(TerminalResult Result, TerminalInfo? Terminal)> OpenAsync(
        TerminalCaller caller, string agentId, string vmId, int cols, int rows, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(caller);
        if (!ValidSize(cols, rows))
        {
            return (new TerminalResult(TerminalRefusal.InvalidSize, "cols and rows must be 1 to 1000"), null);
        }

        if (!_channel.IsAvailable)
        {
            return (new TerminalResult(TerminalRefusal.NoBackend, "no VM backend"), null);
        }

        if (string.IsNullOrWhiteSpace(agentId) || string.IsNullOrWhiteSpace(vmId) || string.IsNullOrWhiteSpace(caller.MemberId))
        {
            return (new TerminalResult(TerminalRefusal.NotFound, "agent, vm and caller ids are required"), null);
        }

        var id = Guid.NewGuid().ToString("N");
        var session = new TerminalSession(id, agentId, vmId, cols, rows, _options, _time);
        if (!Allowed(() => _policy.CanWrite(caller, session.Info())))
        {
            return (new TerminalResult(TerminalRefusal.Forbidden), null);
        }

        lock (_registry)
        {
            var live = _sessions.Values.Where(s => !s.IsExited).ToList();
            if (live.Count >= _options.MaxSessionsPerCompany)
            {
                return (new TerminalResult(TerminalRefusal.LimitReached, "company terminal limit"), null);
            }

            if (live.Count(s => string.Equals(s.AgentId, agentId, StringComparison.Ordinal)) >= _options.MaxSessionsPerAgent)
            {
                return (new TerminalResult(TerminalRefusal.LimitReached, "agent terminal limit"), null);
            }

            _sessions[id] = session;
        }

        IGuestPty pty;
        try
        {
            pty = await _channel.OpenAsync(vmId, id, cols, rows, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            _sessions.TryRemove(id, out _);
            throw;
        }
        catch (Exception ex)
        {
            _sessions.TryRemove(id, out _);
            return (new TerminalResult(TerminalRefusal.NoBackend, "guest open failed: " + ex.GetType().Name), null);
        }

        session.Start(pty, caller.MemberId);
        var humanOpener = !caller.IsAgent;
        if (!await TryAuditAsync("open", caller, id, humanOpener, ct).ConfigureAwait(false)
            || (humanOpener && !await TryAuditAsync("control.take", caller, id, required: true, ct).ConfigureAwait(false)))
        {
            _sessions.TryRemove(id, out _);
            await session.TerminateAsync().ConfigureAwait(false);
            return (new TerminalResult(TerminalRefusal.Forbidden, "audit unavailable"), null);
        }

        return (TerminalResult.Accepted, session.Info());
    }

    /// <inheritdoc />
    public IReadOnlyList<TerminalInfo> List(TerminalCaller caller)
    {
        ArgumentNullException.ThrowIfNull(caller);
        var result = new List<TerminalInfo>();
        foreach (var session in _sessions.Values)
        {
            if (!session.IsReady || Expired(session))
            {
                continue;
            }

            var info = session.Info();
            if (Allowed(() => _policy.CanView(caller, info)))
            {
                result.Add(info);
            }
        }

        result.Sort((a, b) => string.CompareOrdinal(a.TerminalId, b.TerminalId));
        return result;
    }

    /// <inheritdoc />
    public Task<(TerminalResult Result, ITerminalSubscription? Subscription)> SubscribeAsync(
        TerminalCaller caller, string terminalId, long fromSeq, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(caller);
        ct.ThrowIfCancellationRequested();
        if (!TryGet(terminalId, out var session))
        {
            return Task.FromResult<(TerminalResult, ITerminalSubscription?)>((new TerminalResult(TerminalRefusal.NotFound), null));
        }

        if (!Allowed(() => _policy.CanView(caller, session.Info())))
        {
            return Task.FromResult<(TerminalResult, ITerminalSubscription?)>((new TerminalResult(TerminalRefusal.Forbidden), null));
        }

        var (subscription, _) = session.Subscribe(fromSeq);
        return Task.FromResult<(TerminalResult, ITerminalSubscription?)>((TerminalResult.Accepted, subscription));
    }

    /// <inheritdoc />
    public async Task<TerminalResult> WriteAsync(TerminalCaller caller, string terminalId, ReadOnlyMemory<byte> bytes, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(caller);
        if (!TryGet(terminalId, out var session) || session.IsExited)
        {
            return new TerminalResult(TerminalRefusal.NotFound);
        }

        if (!Allowed(() => _policy.CanWrite(caller, session.Info())) || !session.IsWriter(caller.MemberId))
        {
            return new TerminalResult(TerminalRefusal.Forbidden, "not the writer");
        }

        if (bytes.IsEmpty)
        {
            return TerminalResult.Accepted;
        }

        if (session.TryConsumeInput(caller.MemberId, bytes.Length) == InputGrant.RateLimited)
        {
            return new TerminalResult(TerminalRefusal.RateLimited, bytes.Length > _options.InputBytesPerSecond ? "write exceeds the one second burst" : "input rate exceeded");
        }

        if (!caller.IsAgent && session.ShouldAuditHumanInput(_options.AuditEveryHumanInput))
        {
            if (!await TryAuditAsync("input.human", caller, terminalId, required: true, ct).ConfigureAwait(false))
            {
                return new TerminalResult(TerminalRefusal.Forbidden, "audit unavailable");
            }

            session.MarkHumanInputAudited();
        }

        await session.InputGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            // Re-check under the gate: control may have moved while this write waited.
            if (session.IsExited || !session.IsWriter(caller.MemberId) || session.Pty is not { } pty)
            {
                return new TerminalResult(TerminalRefusal.Forbidden, "not the writer");
            }

            try
            {
                var max = _options.MaxFramePayloadBytes;
                for (var offset = 0; offset < bytes.Length; offset += max)
                {
                    await pty.WriteAsync(bytes.Slice(offset, Math.Min(max, bytes.Length - offset)), ct).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception)
            {
                return new TerminalResult(TerminalRefusal.NoBackend, "guest write failed");
            }
        }
        finally
        {
            session.InputGate.Release();
        }

        session.Touch();
        return TerminalResult.Accepted;
    }

    /// <inheritdoc />
    public async Task<TerminalResult> ResizeAsync(TerminalCaller caller, string terminalId, int cols, int rows, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(caller);
        if (!ValidSize(cols, rows))
        {
            return new TerminalResult(TerminalRefusal.InvalidSize, "cols and rows must be 1 to 1000");
        }

        if (!TryGet(terminalId, out var session) || session.IsExited)
        {
            return new TerminalResult(TerminalRefusal.NotFound);
        }

        if (!Allowed(() => _policy.CanWrite(caller, session.Info())) || !session.IsWriter(caller.MemberId) || session.Pty is not { } pty)
        {
            return new TerminalResult(TerminalRefusal.Forbidden, "not the writer");
        }

        try
        {
            await pty.ResizeAsync(cols, rows, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception)
        {
            return new TerminalResult(TerminalRefusal.NoBackend, "guest resize failed");
        }

        session.SetSize(cols, rows);
        return TerminalResult.Accepted;
    }

    /// <inheritdoc />
    public async Task<TerminalResult> TakeControlAsync(TerminalCaller caller, string terminalId, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(caller);
        if (!TryGet(terminalId, out var session) || session.IsExited)
        {
            return new TerminalResult(TerminalRefusal.NotFound);
        }

        if (!Allowed(() => _policy.CanWrite(caller, session.Info())))
        {
            return new TerminalResult(TerminalRefusal.Forbidden);
        }

        switch (session.TryTake(caller.MemberId))
        {
            case TakeOutcome.AlreadyHeld:
                return TerminalResult.Accepted;
            case TakeOutcome.HeldByOther:
                return new TerminalResult(TerminalRefusal.WriterHeld);
        }

        if (!await TryAuditAsync("control.take", caller, terminalId, required: !caller.IsAgent, ct).ConfigureAwait(false))
        {
            session.ClearWriterIf(caller.MemberId);
            return new TerminalResult(TerminalRefusal.Forbidden, "audit unavailable");
        }

        return TerminalResult.Accepted;
    }

    /// <inheritdoc />
    public async Task<TerminalResult> ReleaseControlAsync(TerminalCaller caller, string terminalId, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(caller);
        if (!TryGet(terminalId, out var session) || session.IsExited)
        {
            return new TerminalResult(TerminalRefusal.NotFound);
        }

        // Only the writer, or the owner (a human owner, never an agent), may release.
        switch (session.TryRelease(caller.MemberId, caller.IsOwner && !caller.IsAgent))
        {
            case ReleaseOutcome.Forbidden:
                return new TerminalResult(TerminalRefusal.Forbidden, "not the writer");
            case ReleaseOutcome.NotHeld:
                return TerminalResult.Accepted;
        }

        // Release can only reduce access, so a failed audit write does not undo it.
        await TryAuditAsync("control.release", caller, terminalId, required: false, ct).ConfigureAwait(false);
        return TerminalResult.Accepted;
    }

    /// <inheritdoc />
    public async Task<TerminalResult> CloseAsync(TerminalCaller caller, string terminalId, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(caller);
        if (!TryGet(terminalId, out var session))
        {
            return new TerminalResult(TerminalRefusal.NotFound);
        }

        if (!Allowed(() => _policy.CanWrite(caller, session.Info())))
        {
            return new TerminalResult(TerminalRefusal.Forbidden);
        }

        await CloseSessionAsync(session, caller, ct).ConfigureAwait(false);
        return TerminalResult.Accepted;
    }

    /// <summary>
    /// Closes sessions idle for <see cref="TerminalOptions.IdleClose"/> and forgets ended sessions whose linger has passed. Runs
    /// on a timer and may be called directly.
    /// </summary>
    /// <returns>The number of sessions closed for being idle.</returns>
    public async Task<int> SweepAsync()
    {
        if (Volatile.Read(ref _disposed) != 0)
        {
            return 0;
        }

        var now = _time.GetUtcNow();
        var closed = 0;
        foreach (var session in _sessions.Values)
        {
            if (session.IsExited)
            {
                if (session.RemoveAt <= now)
                {
                    _sessions.TryRemove(session.Id, out _);
                }

                continue;
            }

            if (session.IsReady && now - session.LastActivity >= _options.IdleClose)
            {
                await CloseSessionAsync(session, SystemCaller, CancellationToken.None).ConfigureAwait(false);
                closed++;
            }
        }

        return closed;
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        await _sweeper.DisposeAsync().ConfigureAwait(false);
        foreach (var session in _sessions.Values)
        {
            await session.TerminateAsync().ConfigureAwait(false);
        }

        _sessions.Clear();
    }

    private static bool ValidSize(int cols, int rows) =>
        cols is >= 1 and <= TerminalOptions.MaxDimension && rows is >= 1 and <= TerminalOptions.MaxDimension;

    // A policy that throws denies: the broker never fails open on a broken grant source.
    private static bool Allowed(Func<bool> check)
    {
        try
        {
            return check();
        }
        catch (Exception)
        {
            return false;
        }
    }

    private async Task CloseSessionAsync(TerminalSession session, TerminalCaller caller, CancellationToken ct)
    {
        _sessions.TryRemove(session.Id, out _);
        await session.TerminateAsync().ConfigureAwait(false);
        await TryAuditAsync("close", caller, session.Id, required: false, ct).ConfigureAwait(false);
    }

    private bool Expired(TerminalSession session) => session.IsExited && session.RemoveAt <= _time.GetUtcNow();

    private bool TryGet(string terminalId, out TerminalSession session)
    {
        session = null!;
        if (string.IsNullOrEmpty(terminalId) || !_sessions.TryGetValue(terminalId, out var found) || !found.IsReady)
        {
            return false;
        }

        if (Expired(found))
        {
            _sessions.TryRemove(terminalId, out _);
            return false;
        }

        session = found;
        return true;
    }

    private async Task<bool> TryAuditAsync(string kind, TerminalCaller caller, string terminalId, bool required, CancellationToken ct)
    {
        try
        {
            await _audit.RecordAsync(kind, caller, terminalId, ct).ConfigureAwait(false);
            return true;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            return !required;
        }
    }
}
