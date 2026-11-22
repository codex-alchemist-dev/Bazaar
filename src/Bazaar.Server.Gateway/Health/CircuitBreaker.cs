namespace Bazaar.Server.Gateway.Health;

/// <summary>Where a breaker stands. <c>Open</c> rejects calls until the open duration has passed.</summary>
public enum BreakerState
{
    Closed,
    Open,
    HalfOpen,
}

/// <summary>
/// A consecutive-failure circuit breaker for one provider key. Closed lets every call through. Enough consecutive
/// failures open it. After the open duration it admits a limited number of half-open trial calls, and the trial
/// outcomes decide whether it closes again or reopens with a fresh timer.
/// A half-open outcome counts only for a trial taken with <see cref="TryAcquire"/>, so a stray outcome cannot close it.
/// </summary>
public sealed class CircuitBreaker
{
    private readonly BreakerOptions _options;
    private readonly TimeProvider _time;
    private readonly Lock _gate = new();
    private BreakerState _state = BreakerState.Closed;
    private int _consecutiveFailures;
    private int _trialsInFlight;
    private int _trialSuccesses;
    private DateTimeOffset _openedAt;
    private DateTimeOffset _lastChangedAt;

    public CircuitBreaker(BreakerOptions options, TimeProvider? time = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        Validate(options);
        _options = options;
        _time = time ?? TimeProvider.System;
        _lastChangedAt = _time.GetUtcNow();
    }

    /// <summary>The current state. An open breaker whose duration has passed reports <c>HalfOpen</c>.</summary>
    public BreakerState State
    {
        get
        {
            lock (_gate)
            {
                Refresh();
                return _state;
            }
        }
    }

    /// <summary>Whether <see cref="TryAcquire"/> would succeed now. Takes no trial.</summary>
    public bool WouldAllow()
    {
        lock (_gate)
        {
            Refresh();
            return _state switch
            {
                BreakerState.Closed => true,
                BreakerState.Open => false,
                _ => _trialsInFlight < _options.HalfOpenMaxTrials,
            };
        }
    }

    /// <summary>
    /// Asks to make one call. Closed always allows. Open allows nothing until the open duration has passed, then
    /// moves to HalfOpen. HalfOpen allows up to <see cref="BreakerOptions.HalfOpenMaxTrials"/> calls in flight.
    /// Every <c>true</c> in HalfOpen must be followed by <see cref="RecordSuccess"/>, <see cref="RecordFailure"/>
    /// or <see cref="Abandon"/>.
    /// </summary>
    public bool TryAcquire()
    {
        lock (_gate)
        {
            Refresh();
            switch (_state)
            {
                case BreakerState.Closed:
                    return true;
                case BreakerState.Open:
                    return false;
                default:
                    if (_trialsInFlight >= _options.HalfOpenMaxTrials)
                    {
                        return false;
                    }

                    _trialsInFlight++;
                    return true;
            }
        }
    }

    /// <summary>Records a successful call. In HalfOpen it counts as a trial success.</summary>
    public void RecordSuccess()
    {
        lock (_gate)
        {
            Refresh();
            switch (_state)
            {
                case BreakerState.Closed:
                    _consecutiveFailures = 0;
                    break;
                case BreakerState.HalfOpen when _trialsInFlight > 0:
                    _trialsInFlight--;
                    _trialSuccesses++;
                    if (_trialSuccesses >= _options.SuccessesToClose)
                    {
                        Transition(BreakerState.Closed, _time.GetUtcNow());
                    }

                    break;
            }
        }
    }

    /// <summary>Records a failed call. Enough in a row opens a closed breaker. A failed trial reopens it with a fresh timer.</summary>
    public void RecordFailure()
    {
        lock (_gate)
        {
            Refresh();
            switch (_state)
            {
                case BreakerState.Closed:
                    _consecutiveFailures++;
                    if (_consecutiveFailures >= _options.FailureThreshold)
                    {
                        Open(_time.GetUtcNow());
                    }

                    break;
                case BreakerState.HalfOpen when _trialsInFlight > 0:
                    Open(_time.GetUtcNow());
                    break;
            }
        }
    }

    /// <summary>
    /// An acquired call ended without a health verdict, for example because the caller cancelled it. Releases a
    /// half-open trial so the breaker does not stay blocked. Has no effect otherwise.
    /// </summary>
    public void Abandon()
    {
        lock (_gate)
        {
            Refresh();
            if (_state == BreakerState.HalfOpen && _trialsInFlight > 0)
            {
                _trialsInFlight--;
            }
        }
    }

    /// <summary>A consistent read of the state, the failure run and the time of the last state change.</summary>
    public (BreakerState State, int ConsecutiveFailures, DateTimeOffset LastChangedAt) Snapshot()
    {
        lock (_gate)
        {
            Refresh();
            return (_state, _consecutiveFailures, _lastChangedAt);
        }
    }

    private void Refresh()
    {
        if (_state == BreakerState.Open && _time.GetUtcNow() - _openedAt >= _options.OpenDuration)
        {
            Transition(BreakerState.HalfOpen, _openedAt + _options.OpenDuration);
        }
    }

    private void Open(DateTimeOffset at)
    {
        _openedAt = at;
        Transition(BreakerState.Open, at);
    }

    private void Transition(BreakerState next, DateTimeOffset at)
    {
        _state = next;
        _lastChangedAt = at;
        _trialsInFlight = 0;
        _trialSuccesses = 0;
        if (next == BreakerState.Closed)
        {
            _consecutiveFailures = 0;
        }
    }

    private static void Validate(BreakerOptions options)
    {
        if (options.FailureThreshold < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(options), "The failure threshold must be at least 1.");
        }

        if (options.OpenDuration < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(options), "The open duration cannot be negative.");
        }

        if (options.HalfOpenMaxTrials < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(options), "The half-open trial limit must be at least 1.");
        }

        if (options.SuccessesToClose < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(options), "The successes needed to close must be at least 1.");
        }

        if (options.ProbeInterval <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(options), "The probe interval must be positive.");
        }
    }
}
