using System.Globalization;
using Bazaar.Server.BrowserPool.Abstractions;
using Microsoft.Extensions.Options;

namespace Bazaar.Server.BrowserPool;

/// <summary>
/// The pool. A short lock guards the bookkeeping (slots, sessions, quarantine) and is never held across an await, so the kill
/// switch (<see cref="CloseAllForAgentAsync"/>) never queues behind a slow open: it cancels the open, stops the slot's processes
/// and wipes the slot at once, and the open, when it wakes up, runs the cleanup a second time and then frees the slot.
/// A slot is freed only after its cleanup commands all succeeded; otherwise it is quarantined (counts against the pool size, audited,
/// retried by the sweep). Every refusal fails closed.
/// </summary>
public sealed class BrowserPoolService : IBrowserPool, IDisposable
{
    /// <summary>Upper bound for stopping a browser and running cleanup. Cleanup is not tied to the caller's cancellation, so it needs its own limit.</summary>
    private static readonly TimeSpan _cleanupTimeout = TimeSpan.FromSeconds(60);

    private readonly BrowserPoolOptions _options;
    private readonly IPoolVmHost _host;
    private readonly IPoolEgressPolicy _egress;
    private readonly IPoolGate _gate;
    private readonly IAsyncPoolGate? _asyncGate;
    private readonly IPoolAuditSink _audit;
    private readonly TimeProvider _time;
    private readonly IPoolReadinessCheck[] _readiness;

    private readonly Lock _lock = new();
    private readonly SemaphoreSlim _firewallLock = new(1, 1);
    private readonly Dictionary<string, SessionState> _sessions = new(StringComparer.Ordinal);
    private readonly SessionState?[] _slots;
    private readonly Dictionary<int, Quarantine> _quarantined = [];

    /// <summary>Creates the pool. Throws when the options are invalid.</summary>
    public BrowserPoolService(
        IOptions<BrowserPoolOptions> options,
        IPoolVmHost host,
        IPoolEgressPolicy egress,
        IPoolGate gate,
        IPoolAuditSink audit,
        TimeProvider time,
        IEnumerable<IPoolReadinessCheck>? readiness = null)
        IEnumerable<IPoolReadinessCheck>? readiness = null,
        IAsyncPoolGate? asyncGate = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        _options = options.Value;
        var errors = BrowserPoolOptionsValidator.Check(_options);
        if (errors.Count != 0)
        {
            throw new ArgumentException("Invalid browser pool options: " + string.Join(' ', errors), nameof(options));
        }

        _host = host ?? throw new ArgumentNullException(nameof(host));
        _egress = egress ?? throw new ArgumentNullException(nameof(egress));
        _gate = gate ?? throw new ArgumentNullException(nameof(gate));
        _asyncGate = asyncGate;
        _audit = audit ?? throw new ArgumentNullException(nameof(audit));
        _time = time ?? throw new ArgumentNullException(nameof(time));
        _readiness = readiness?.ToArray() ?? [];
        _slots = new SessionState?[_options.MaxSessions];
    }

    /// <summary>Releases the firewall gate. Open sessions are not closed; the host closes them through the pool before it disposes it.</summary>
    public void Dispose() => _firewallLock.Dispose();

    /// <inheritdoc />
    public async Task<PoolSession> OpenAsync(PoolSessionRequest request, CancellationToken ct)
    {
        var agentForAudit = request is not null && PoolRequestValidator.IsValidAgentId(request.AgentId) ? request.AgentId : "invalid";

        if (!_options.Enabled)
        {
            throw Refuse(agentForAudit, PoolFailure.Disabled, "The browser pool is switched off.");
        }

        var requestError = PoolRequestValidator.Validate(request, out _);
        if (requestError is not null)
        {
            throw Refuse(agentForAudit, PoolFailure.InvalidRequest, requestError);
        }

        if (await IsBlockedAsync(request!.AgentId, ct).ConfigureAwait(false))
        {
            throw Refuse(request.AgentId, PoolFailure.AgentBlocked, "The agent is blocked.");
        }

        var limit = CheckLimits(request.AgentId);
        if (limit is not null)
        {
            throw Refuse(request.AgentId, limit.Value, LimitMessage(limit.Value));
        }

        var notReady = await CheckReadinessAsync(ct).ConfigureAwait(false);
        if (notReady is not null)
        {
            throw Refuse(request.AgentId, PoolFailure.ImageNotReady, notReady);
        }

        PoolEgressEndpoint? endpoint;
        try
        {
            endpoint = await _egress.ResolveAsync(request.AgentId, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (ex is not BrowserPoolException)
        {
            throw Refuse(request.AgentId, PoolFailure.NoEgress, "The egress policy failed: " + ex.GetType().Name);
        }

        var endpointError = PoolRequestValidator.ValidateEndpoint(endpoint);
        if (endpointError is not null || endpoint is null)
        {
            throw Refuse(request.AgentId, PoolFailure.NoEgress, endpointError ?? "No egress endpoint.");
        }

        var state = Allocate(request, out var allocationFailure);
        if (state is null)
        {
            throw Refuse(request.AgentId, allocationFailure!.Value, LimitMessage(allocationFailure.Value));
        }

        return await RunOpenAsync(state, request, endpoint, ct).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task CloseAsync(string sessionId, CancellationToken ct)
    {
        SessionState? state;
        lock (_lock)
        {
            _sessions.TryGetValue(sessionId ?? string.Empty, out state);
        }

        if (state is null)
        {
            return;
        }

        await TeardownAsync(state, PoolCloseReason.Requested, ct).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<int> CloseAllForAgentAsync(string agentId, CancellationToken ct)
    {
        if (!PoolRequestValidator.IsValidAgentId(agentId))
        {
            return 0;
        }

        List<SessionState> targets;
        lock (_lock)
        {
            targets = _sessions.Values.Where(s => s.AgentId == agentId).ToList();
        }

        await Task.WhenAll(targets.Select(s => TeardownAsync(s, PoolCloseReason.KillSwitch, ct))).ConfigureAwait(false);
        return targets.Count;
    }

    /// <inheritdoc />
    public IReadOnlyList<PoolSession> Sessions(string agentId)
    {
        lock (_lock)
        {
            return _sessions.Values
                .Where(s => s.AgentId == agentId && s.Status == SessionStatus.Open)
                .Select(s => s.ToSession())
                .OrderBy(s => s.Slot)
                .ToList();
        }
    }

    /// <inheritdoc />
    public void Touch(string sessionId)
    {
        lock (_lock)
        {
            if (sessionId is not null && _sessions.TryGetValue(sessionId, out var state) && state.Status == SessionStatus.Open)
            {
                state.LastActivity = _time.GetUtcNow();
            }
        }
    }

    /// <inheritdoc />
    public PoolSnapshot Snapshot()
    {
        lock (_lock)
        {
            var open = _sessions.Values.Where(s => s.Status == SessionStatus.Open).Select(s => s.ToSession()).OrderBy(s => s.Slot).ToList();
            return new PoolSnapshot(
                _options.MaxSessions,
                open.Count,
                _sessions.Values.Count(s => s.Status == SessionStatus.Opening),
                open,
                _quarantined.Values.Select(q => q.Info).OrderBy(q => q.Slot).ToList(),
                ActiveRulesLocked());
        }
    }

    /// <inheritdoc />
    public async Task<int> SweepAsync(CancellationToken ct)
    {
        var now = _time.GetUtcNow();
        var due = new List<(SessionState State, PoolCloseReason Reason, bool WasOpen)>();
        lock (_lock)
        {
            foreach (var s in _sessions.Values)
            {
                if (s.TeardownTcs is not null)
                {
                    continue;
                }

                if (s.Status == SessionStatus.Open)
                {
                    if (now >= s.ExpiresAt)
                    {
                        due.Add((s, PoolCloseReason.Expired, true));
                    }
                    else if (now - s.LastActivity >= _options.IdleTimeout)
                    {
                        due.Add((s, PoolCloseReason.IdleTimeout, true));
                    }
                }
                else if (s.Status == SessionStatus.Opening && now - s.OpenStartedAt > _options.OpenTimeout)
                {
                    due.Add((s, PoolCloseReason.OpenFailed, false));
                }
            }
        }

        await Task.WhenAll(due.Select(d => TeardownAsync(d.State, d.Reason, ct))).ConfigureAwait(false);
        await RetryQuarantinedAsync().ConfigureAwait(false);
        return due.Count(d => d.WasOpen);
    }

    /// <summary>Asks every registered readiness check. Returns a refusal message naming the failure ids, or null when all pass. A check that throws counts as a failure.</summary>
    private async Task<string?> CheckReadinessAsync(CancellationToken ct)
    {
        if (_readiness.Length == 0)
        {
            return null;
        }

        var ids = new List<string>();
        foreach (var check in _readiness)
        {
            try
            {
                foreach (var failure in await check.CheckAsync(ct).ConfigureAwait(false))
                {
                    ids.Add(failure.Id);
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception)
            {
                ids.Add("readiness-check-error");
            }
        }

        return ids.Count == 0 ? null : "The pool VM image is not ready: " + string.Join(", ", ids.Distinct(StringComparer.Ordinal)) + ".";
    }

    /// <summary>
    /// Asks the gate: the asynchronous one when the host registered it, otherwise the synchronous one. A gate that throws (other than
    /// by cancellation) counts as blocked, so a broken gate refuses and never lets a session through.
    /// </summary>
    private async ValueTask<bool> IsBlockedAsync(string agentId, CancellationToken ct)
    {
        try
        {
            return _asyncGate is not null
                ? await _asyncGate.IsBlockedAsync(agentId, ct).ConfigureAwait(false)
                : _gate.IsBlocked(agentId);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            return true;
        }
    }

    private BrowserPoolException Refuse(string agentId, PoolFailure failure, string detail)
    {
        SafeAudit(() => _audit.Refused(agentId, failure, detail));
        return new BrowserPoolException(failure, detail);
    }

    private static string LimitMessage(PoolFailure failure) =>
        failure == PoolFailure.AgentLimit ? "The agent is at its session limit." : "The browser pool is full.";

    private PoolFailure? CheckLimits(string agentId)
    {
        lock (_lock)
        {
            return CheckLimitsLocked(agentId);
        }
    }

    private PoolFailure? CheckLimitsLocked(string agentId)
    {
        if (_sessions.Values.Count(s => s.AgentId == agentId) >= _options.MaxSessionsPerAgent)
        {
            return PoolFailure.AgentLimit;
        }

        if (_sessions.Count + _quarantined.Count >= _options.MaxSessions)
        {
            return PoolFailure.PoolFull;
        }

        return null;
    }

    /// <summary>Takes the lowest free slot and registers the session, in one short lock. Null with the reason when the limits say no.</summary>
    private SessionState? Allocate(PoolSessionRequest request, out PoolFailure? failure)
    {
        lock (_lock)
        {
            failure = CheckLimitsLocked(request.AgentId);
            if (failure is not null)
            {
                return null;
            }

            for (var slot = 0; slot < _slots.Length; slot++)
            {
                if (_slots[slot] is null && !_quarantined.ContainsKey(slot))
                {
                    var state = new SessionState(
                        Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture),
                        request.AgentId,
                        slot,
                        request.Display,
                        _time.GetUtcNow(),
                        PoolCommandLine.CleanupCommands(_options, slot));
                    _slots[slot] = state;
                    _sessions[state.SessionId] = state;
                    return state;
                }
            }

            failure = PoolFailure.PoolFull;
            return null;
        }
    }

    private async Task<PoolSession> RunOpenAsync(SessionState state, PoolSessionRequest request, PoolEgressEndpoint endpoint, CancellationToken ct)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, state.Cts.Token);
        linked.CancelAfter(_options.OpenTimeout);
        var token = linked.Token;

        Exception? failure = null;
        PoolProcess? process = null;
        try
        {
            var spec = PoolCommandLine.Build(_options, state.SessionId, state.Slot, request, endpoint);
            state.Rule = spec.Firewall;

            foreach (var command in spec.Prepare)
            {
                await _host.RunAsync(command, token).ConfigureAwait(false);
            }

            // The kill switch may have fired while the slot was being prepared.
            if (await IsBlockedAsync(request.AgentId, token).ConfigureAwait(false))
            {
                throw new BrowserPoolException(PoolFailure.AgentBlocked, "The agent is blocked.");
            }

            token.ThrowIfCancellationRequested();
            await ApplyFirewallAsync(token).ConfigureAwait(false);
            token.ThrowIfCancellationRequested();
            process = await _host.StartAsync(spec, token).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            failure = ex;
        }

        if (failure is null)
        {
            bool closing;
            PoolSession? session = null;
            lock (_lock)
            {
                closing = state.TeardownTcs is not null;
                if (!closing)
                {
                    var now = _time.GetUtcNow();
                    state.Process = process;
                    state.StartedAt = now;
                    state.ExpiresAt = now + _options.SessionLifetime;
                    state.LastActivity = now;
                    state.Status = SessionStatus.Open;
                    state.Opened = true;
                    state.OpenInFlight = false;
                    session = state.ToSession();
                }
            }

            if (!closing)
            {
                state.OpenDone.TrySetResult();
                SafeAudit(() => _audit.Opened(session!));
                return session!;
            }

            // Closed while the browser was starting: it exists now, so stop it. The teardown's second cleanup removes the rest.
            await StopQuietlyAsync(process!).ConfigureAwait(false);
            state.OpenDone.TrySetResult();
            await state.Settled.Task.ConfigureAwait(false);
            throw ClosedWhileOpening(state);
        }

        bool closedByOther;
        lock (_lock)
        {
            closedByOther = state.TeardownTcs is not null;
            if (!closedByOther)
            {
                state.OpenInFlight = false;
            }
        }

        state.OpenDone.TrySetResult();
        await TeardownAsync(state, PoolCloseReason.OpenFailed, CancellationToken.None).ConfigureAwait(false);
        await state.Settled.Task.ConfigureAwait(false);

        if (failure is OperationCanceledException && ct.IsCancellationRequested)
        {
            throw new OperationCanceledException(ct);
        }

        if (closedByOther)
        {
            throw ClosedWhileOpening(state);
        }

        var detail = failure is BrowserPoolException bpe ? bpe.Message : "The browser could not be started: " + failure.GetType().Name;
        var kind = failure is BrowserPoolException known ? known.Failure : PoolFailure.LaunchFailed;
        SafeAudit(() => _audit.Refused(state.AgentId, kind, detail));
        throw new BrowserPoolException(kind, detail, failure);
    }

    private BrowserPoolException ClosedWhileOpening(SessionState state)
    {
        var kind = state.Reason == PoolCloseReason.KillSwitch ? PoolFailure.AgentBlocked : PoolFailure.LaunchFailed;
        var detail = "The session was closed while it was starting (" + state.Reason + ").";
        SafeAudit(() => _audit.Refused(state.AgentId, kind, detail));
        return new BrowserPoolException(kind, detail);
    }

    /// <summary>
    /// Ends a session once: the first caller does the work, later callers wait for it. Cancels an open in flight, stops the browser,
    /// runs the cleanup commands, and frees or quarantines the slot. It never throws for host failures.
    /// </summary>
    private async Task TeardownAsync(SessionState state, PoolCloseReason reason, CancellationToken ct)
    {
        TaskCompletionSource? existing = null;
        var owner = false;
        var needSecondCleanup = false;
        PoolProcess? process = null;
        lock (_lock)
        {
            if (state.TeardownTcs is { } tcs)
            {
                existing = tcs;
            }
            else
            {
                state.TeardownTcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                state.Reason = reason;
                state.Status = SessionStatus.Closing;
                owner = true;
                // An open that is still running may create files after this cleanup, so it needs a second pass once the open ends.
                needSecondCleanup = state.OpenInFlight;
                process = state.Process;
            }
        }

        if (!owner)
        {
            await existing!.Task.WaitAsync(ct).ConfigureAwait(false);
            return;
        }

        try
        {
            try
            {
                await state.Cts.CancelAsync().ConfigureAwait(false);
            }
            catch (ObjectDisposedException)
            {
            }

            if (process is not null)
            {
                await StopQuietlyAsync(process).ConfigureAwait(false);
            }

            var cleanupOk = await RunCleanupAsync(state.Cleanup).ConfigureAwait(false);
            if (needSecondCleanup)
            {
                _ = SettleAfterOpenAsync(state, cleanupOk);
            }
            else
            {
                await FinalizeAsync(state, cleanupOk, "Cleanup failed.").ConfigureAwait(false);
            }
        }
        finally
        {
            state.TeardownTcs!.TrySetResult();
        }
    }

    private async Task SettleAfterOpenAsync(SessionState state, bool firstCleanupOk)
    {
        try
        {
            await state.OpenDone.Task.ConfigureAwait(false);
            var secondOk = await RunCleanupAsync(state.Cleanup).ConfigureAwait(false);
            await FinalizeAsync(state, firstCleanupOk && secondOk, "Cleanup failed.").ConfigureAwait(false);
        }
        catch (Exception)
        {
            // Never leave a slot half-owned: anything unexpected quarantines it.
            await FinalizeAsync(state, false, "Cleanup did not finish.").ConfigureAwait(false);
        }
    }

    private async Task<bool> RunCleanupAsync(IReadOnlyList<PoolCommand> cleanup)
    {
        using var cts = new CancellationTokenSource(_cleanupTimeout);
        var ok = true;
        foreach (var command in cleanup)
        {
            try
            {
                await _host.RunAsync(command, cts.Token).ConfigureAwait(false);
            }
            catch (Exception)
            {
                // Keep going: every cleanup command runs even when an earlier one failed.
                ok = false;
            }
        }

        return ok;
    }

    private async Task StopQuietlyAsync(PoolProcess process)
    {
        using var cts = new CancellationTokenSource(_cleanupTimeout);
        try
        {
            await _host.StopAsync(process, cts.Token).ConfigureAwait(false);
        }
        catch (Exception)
        {
            // The cleanup commands kill everything the slot user owns, so a failed stop is not the last line.
        }
    }

    private async Task FinalizeAsync(SessionState state, bool cleanupOk, string failureDetail)
    {
        PoolSession? closed = null;
        lock (_lock)
        {
            _sessions.Remove(state.SessionId);
            _slots[state.Slot] = null;
            if (!cleanupOk)
            {
                _quarantined[state.Slot] = new Quarantine(
                    new PoolQuarantinedSlot(state.Slot, state.SessionId, state.AgentId, _time.GetUtcNow(), failureDetail),
                    state.Cleanup);
            }

            if (state.Opened)
            {
                closed = state.ToSession();
            }
        }

        if (closed is not null)
        {
            SafeAudit(() => _audit.Closed(closed, state.Reason));
        }

        if (!cleanupOk)
        {
            SafeAudit(() => _audit.SlotQuarantined(state.Slot, state.SessionId, state.AgentId, failureDetail));
        }

        state.Settled.TrySetResult();
        await RefreshFirewallQuietlyAsync().ConfigureAwait(false);
    }

    private async Task RetryQuarantinedAsync()
    {
        List<Quarantine> snapshot;
        lock (_lock)
        {
            snapshot = _quarantined.Values.ToList();
        }

        foreach (var q in snapshot)
        {
            if (await RunCleanupAsync(q.Cleanup).ConfigureAwait(false))
            {
                lock (_lock)
                {
                    _quarantined.Remove(q.Info.Slot);
                }
            }
        }
    }

    private List<PoolFirewallRule> ActiveRulesLocked() =>
        _sessions.Values
            .Where(s => s.TeardownTcs is null && s.Rule is not null)
            .Select(s => s.Rule!)
            .OrderBy(r => r.Uid)
            .ToList();

    /// <summary>Pushes the full rule set for the sessions that exist now. Serialised, and the set is computed inside the gate so a late call cannot overwrite a newer one.</summary>
    private async Task ApplyFirewallAsync(CancellationToken ct)
    {
        await _firewallLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            List<PoolFirewallRule> rules;
            lock (_lock)
            {
                rules = ActiveRulesLocked();
            }

            await _host.ApplyFirewallAsync(rules, ct).ConfigureAwait(false);
        }
        finally
        {
            _firewallLock.Release();
        }
    }

    private async Task RefreshFirewallQuietlyAsync()
    {
        using var cts = new CancellationTokenSource(_cleanupTimeout);
        try
        {
            await ApplyFirewallAsync(cts.Token).ConfigureAwait(false);
        }
        catch (Exception)
        {
            // A stale accept rule for a uid with no processes is harmless, and the next open recomputes the whole set.
        }
    }

    private static void SafeAudit(Action write)
    {
        try
        {
            write();
        }
        catch (Exception)
        {
            // An audit sink must not break a close or the kill switch.
        }
    }

    private enum SessionStatus
    {
        Opening,
        Open,
        Closing,
    }

    private sealed record Quarantine(PoolQuarantinedSlot Info, IReadOnlyList<PoolCommand> Cleanup);

    private sealed class SessionState
    {
        public SessionState(string sessionId, string agentId, int slot, PoolDisplayMode display, DateTimeOffset now, IReadOnlyList<PoolCommand> cleanup)
        {
            SessionId = sessionId;
            AgentId = agentId;
            Slot = slot;
            Display = display;
            OpenStartedAt = now;
            StartedAt = now;
            ExpiresAt = now;
            LastActivity = now;
            Cleanup = cleanup;
        }

        public string SessionId { get; }

        public string AgentId { get; }

        public int Slot { get; }

        public PoolDisplayMode Display { get; }

        public IReadOnlyList<PoolCommand> Cleanup { get; }

        public DateTimeOffset OpenStartedAt { get; }

        public DateTimeOffset StartedAt { get; set; }

        public DateTimeOffset ExpiresAt { get; set; }

        public DateTimeOffset LastActivity { get; set; }

        public SessionStatus Status { get; set; } = SessionStatus.Opening;

        public PoolProcess? Process { get; set; }

        public PoolFirewallRule? Rule { get; set; }

        public bool Opened { get; set; }

        public bool OpenInFlight { get; set; } = true;

        public PoolCloseReason Reason { get; set; }

        public CancellationTokenSource Cts { get; } = new();

        public TaskCompletionSource OpenDone { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource Settled { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource? TeardownTcs { get; set; }

        public PoolSession ToSession() => new(SessionId, AgentId, Slot, Display, StartedAt, ExpiresAt);
    }
}
