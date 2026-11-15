namespace Bazaar.Conduit.Security;

/// <summary>
/// Per-listener request rate limits and failed-attempt lockout for Conduit listeners. Doc 39 section 3 (rate limits
/// apply to every Conduit listener and a Conduit cannot raise them) and doc 49 section 5 (lockout and backoff).
/// State is kept in memory per (listener, client key). A success clears the failure count and the backoff level.
/// Idle state is pruned, and a backoff level is forgotten after <see cref="ConduitAccessOptions.MaxLockout"/> of quiet time.
/// </summary>
public sealed class ConduitAccessGuard
{
    /// <summary>Caps the backoff level so the doubling never overflows. The duration is capped before this matters.</summary>
    private const int _maxLockoutLevel = 32;

    private readonly ConduitAccessOptions _options;
    private readonly TimeProvider _time;
    private readonly TimeSpan _pruneInterval;
    private readonly Dictionary<(string ListenerId, string ClientKey), ClientState> _states = new();
    private readonly Lock _gate = new();
    private DateTimeOffset _nextPrune;

    /// <summary>Creates a guard. Options that are invalid or looser than the shipped floor are rejected.</summary>
    public ConduitAccessGuard(ConduitAccessOptions options, TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(options);

        var problems = options.Validate().Concat(options.ValidateAgainstFloor()).ToList();
        if (problems.Count > 0)
        {
            throw new ArgumentException(string.Join(' ', problems), nameof(options));
        }

        _options = options;
        _time = timeProvider ?? TimeProvider.System;
        _pruneInterval = options.RequestWindow > options.FailureWindow ? options.RequestWindow : options.FailureWindow;
        _nextPrune = _time.GetUtcNow() + _pruneInterval;
    }

    /// <summary>The options this guard enforces.</summary>
    public ConduitAccessOptions Options => _options;

    /// <summary>Number of (listener, client) pairs with state held in memory. Diagnostic; used by tests and Kodo.</summary>
    public int TrackedClientCount
    {
        get
        {
            lock (_gate)
            {
                return _states.Count;
            }
        }
    }

    /// <summary>
    /// Checks a request before it is handled. A request is admitted when the client is not locked out and has fewer than
    /// <see cref="ConduitAccessOptions.RequestLimit"/> admitted requests in the last <see cref="ConduitAccessOptions.RequestWindow"/>.
    /// Denied requests are not counted, so retrying at <see cref="ConduitAccessDecision.RetryAfter"/> succeeds.
    /// </summary>
    public ConduitAccessDecision TryAdmitRequest(string listenerId, string clientKey)
    {
        ValidateKey(listenerId, clientKey);

        lock (_gate)
        {
            var now = _time.GetUtcNow();
            PruneIfDue(now);
            var state = GetOrAdd(listenerId, clientKey, now);
            Touch(state, now);

            if (IsLockedOut(state, now))
            {
                return ConduitAccessDecision.Denied(ConduitDenyReason.LockedOut, state.LockedUntil!.Value - now);
            }

            if (state.Requests.Count >= _options.RequestLimit)
            {
                var retryAfter = state.Requests.Peek() + _options.RequestWindow - now;
                return ConduitAccessDecision.Denied(ConduitDenyReason.RateLimited, retryAfter);
            }

            state.Requests.Enqueue(now);
            return ConduitAccessDecision.Allowed;
        }
    }

    /// <summary>
    /// Records the result of an authentication attempt. A success clears the failure count and the backoff level. A failure
    /// is counted inside <see cref="ConduitAccessOptions.FailureWindow"/>; reaching <see cref="ConduitAccessOptions.FailureThreshold"/>
    /// starts a lockout of <see cref="ConduitAccessOptions.BaseLockout"/>, doubled for each repeat lockout up to
    /// <see cref="ConduitAccessOptions.MaxLockout"/>. Returns Denied when the client is locked out after this result, with the
    /// time left in the lockout. While a lockout is active, results are refused and do not change its length.
    /// </summary>
    public ConduitAccessDecision RecordAuthResult(string listenerId, string clientKey, bool success)
    {
        ValidateKey(listenerId, clientKey);

        lock (_gate)
        {
            var now = _time.GetUtcNow();
            PruneIfDue(now);
            var state = GetOrAdd(listenerId, clientKey, now);
            Touch(state, now);

            if (IsLockedOut(state, now))
            {
                return ConduitAccessDecision.Denied(ConduitDenyReason.LockedOut, state.LockedUntil!.Value - now);
            }

            if (success)
            {
                state.Failures.Clear();
                state.LockoutLevel = 0;
                return ConduitAccessDecision.Allowed;
            }

            state.Failures.Enqueue(now);
            if (state.Failures.Count < _options.FailureThreshold)
            {
                return ConduitAccessDecision.Allowed;
            }

            state.Failures.Clear();
            state.LockoutLevel = Math.Min(state.LockoutLevel + 1, _maxLockoutLevel);
            var duration = LockoutFor(state.LockoutLevel);
            state.LockedUntil = now + duration;
            return ConduitAccessDecision.Denied(ConduitDenyReason.LockedOut, duration);
        }
    }

    /// <summary>Lockout length for the given repeat level (1 is the first lockout): base doubled per repeat, capped at the maximum.</summary>
    private TimeSpan LockoutFor(int level)
    {
        var duration = _options.BaseLockout;
        for (var i = 1; i < level && duration < _options.MaxLockout; i++)
        {
            duration += duration;
        }

        return duration < _options.MaxLockout ? duration : _options.MaxLockout;
    }

    private static bool IsLockedOut(ClientState state, DateTimeOffset now) =>
        state.LockedUntil is { } until && until > now;

    /// <summary>
    /// Drops expired request and failure entries, ends an expired lockout, and forgets the backoff level after
    /// <see cref="ConduitAccessOptions.MaxLockout"/> without activity once the lockout has ended.
    /// </summary>
    private void Trim(ClientState state, DateTimeOffset now)
    {
        var quietSince = state.LockedUntil is { } until && until > state.LastActivity ? until : state.LastActivity;
        if (state.LockoutLevel > 0 && !IsLockedOut(state, now) && now - quietSince >= _options.MaxLockout)
        {
            state.LockoutLevel = 0;
        }

        if (state.LockedUntil is { } ended && ended <= now)
        {
            state.LockedUntil = null;
        }

        var requestCutoff = now - _options.RequestWindow;
        while (state.Requests.Count > 0 && state.Requests.Peek() <= requestCutoff)
        {
            state.Requests.Dequeue();
        }

        var failureCutoff = now - _options.FailureWindow;
        while (state.Failures.Count > 0 && state.Failures.Peek() <= failureCutoff)
        {
            state.Failures.Dequeue();
        }
    }

    private void Touch(ClientState state, DateTimeOffset now)
    {
        Trim(state, now);
        state.LastActivity = now;
    }

    private ClientState GetOrAdd(string listenerId, string clientKey, DateTimeOffset now)
    {
        var key = (listenerId, clientKey);
        if (!_states.TryGetValue(key, out var state))
        {
            state = new ClientState(now);
            _states[key] = state;
        }

        return state;
    }

    /// <summary>Removes state with no live requests, failures, lockout or backoff level. Runs at most once per prune interval.</summary>
    private void PruneIfDue(DateTimeOffset now)
    {
        if (now < _nextPrune)
        {
            return;
        }

        _nextPrune = now + _pruneInterval;
        var idle = new List<(string ListenerId, string ClientKey)>();
        foreach (var (key, state) in _states)
        {
            Trim(state, now);
            if (state.Requests.Count == 0 && state.Failures.Count == 0 && state.LockedUntil is null && state.LockoutLevel == 0)
            {
                idle.Add(key);
            }
        }

        foreach (var key in idle)
        {
            _states.Remove(key);
        }
    }

    private static void ValidateKey(string listenerId, string clientKey)
    {
        ArgumentException.ThrowIfNullOrEmpty(listenerId);
        ArgumentException.ThrowIfNullOrEmpty(clientKey);
    }

    /// <summary>Per (listener, client) state: admitted request times, failure times, lockout end and backoff level.</summary>
    private sealed class ClientState(DateTimeOffset createdAt)
    {
        public Queue<DateTimeOffset> Requests { get; } = new();
        public Queue<DateTimeOffset> Failures { get; } = new();
        public int LockoutLevel { get; set; }
        public DateTimeOffset? LockedUntil { get; set; }
        public DateTimeOffset LastActivity { get; set; } = createdAt;
    }
}
