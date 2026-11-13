namespace Bazaar.Kodo.Probes;

/// <summary>The three breaker states from doc 46 section 7.4.</summary>
public enum BreakerState
{
    Closed,
    Open,
    HalfOpen,
}

/// <summary>
/// The admission record a caller gets from <see cref="CircuitBreaker.TryEnter"/>. It carries the state
/// generation it was admitted under, so a call that finishes after the state changed cannot move the breaker.
/// </summary>
public readonly record struct BreakerTicket(int Generation, bool IsTrial);

/// <summary>
/// A per-target circuit breaker (doc 46 section 7.4). It opens after N consecutive failures (default 5). After
/// the cooldown (default 60 seconds) it goes half-open and admits exactly one trial call. A passing trial closes
/// it; a failing trial opens it again with a new cooldown.
/// </summary>
/// <remarks>
/// Every state change happens under one lock, not Interlocked. An admission decision reads and writes several
/// fields together (state, generation, trial flag, opened-at time), and one compare-exchange cannot update them
/// as a single step. The lock is held only for those field updates, never across a probe call, so the
/// single-trial rule holds even when callers race.
/// The error-ratio window in section 7.4 is not implemented yet. Only consecutive failures open the breaker.
/// </remarks>
public sealed class CircuitBreaker
{
    public const int DefaultFailureThreshold = 5;

    public static readonly TimeSpan DefaultCooldown = TimeSpan.FromSeconds(60);

    private readonly object _gate = new();
    private readonly int _failureThreshold;
    private readonly TimeSpan _cooldown;
    private readonly TimeProvider _timeProvider;

    private BreakerState _state = BreakerState.Closed;
    private int _consecutiveFailures;
    private DateTimeOffset _openedAt;
    private bool _trialInFlight;
    private int _generation;

    public CircuitBreaker(int failureThreshold = DefaultFailureThreshold, TimeSpan? cooldown = null, TimeProvider? timeProvider = null)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(failureThreshold, 1);

        _failureThreshold = failureThreshold;
        _cooldown = cooldown ?? DefaultCooldown;
        ArgumentOutOfRangeException.ThrowIfLessThan(_cooldown, TimeSpan.Zero);
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    /// <summary>The current state. An open breaker whose cooldown has passed reads as half-open.</summary>
    public BreakerState State
    {
        get
        {
            lock (_gate)
            {
                RefreshLocked();
                return _state;
            }
        }
    }

    /// <summary>
    /// Asks to make a call. Closed admits every caller. Half-open admits one trial and refuses the rest until
    /// that trial is recorded. Open refuses.
    /// </summary>
    public bool TryEnter(out BreakerTicket ticket)
    {
        lock (_gate)
        {
            RefreshLocked();
            switch (_state)
            {
                case BreakerState.Closed:
                    ticket = new BreakerTicket(_generation, IsTrial: false);
                    return true;

                case BreakerState.HalfOpen when !_trialInFlight:
                    _trialInFlight = true;
                    ticket = new BreakerTicket(_generation, IsTrial: true);
                    return true;

                default:
                    ticket = default;
                    return false;
            }
        }
    }

    /// <summary>Records the outcome of an admitted call. A ticket from an earlier state is ignored.</summary>
    public void Record(BreakerTicket ticket, bool failed)
    {
        lock (_gate)
        {
            if (ticket.Generation != _generation)
            {
                return;
            }

            if (ticket.IsTrial)
            {
                _trialInFlight = false;
                if (failed)
                {
                    OpenLocked();
                }
                else
                {
                    CloseLocked();
                }

                return;
            }

            if (!failed)
            {
                _consecutiveFailures = 0;
                return;
            }

            _consecutiveFailures++;
            if (_consecutiveFailures >= _failureThreshold)
            {
                OpenLocked();
            }
        }
    }

    /// <summary>
    /// Frees the trial slot when the trial never ran, for example when the pool was full or the caller cancelled.
    /// The breaker stays half-open, so another caller can run the trial. It is not counted as a failure.
    /// </summary>
    public void ReleaseTrial(BreakerTicket ticket)
    {
        lock (_gate)
        {
            if (ticket.IsTrial && ticket.Generation == _generation)
            {
                _trialInFlight = false;
            }
        }
    }

    // Open becomes half-open once the cooldown has passed. This is the only time-driven change.
    private void RefreshLocked()
    {
        if (_state == BreakerState.Open && _timeProvider.GetUtcNow() - _openedAt >= _cooldown)
        {
            _state = BreakerState.HalfOpen;
            _trialInFlight = false;
            _generation++;
        }
    }

    private void OpenLocked()
    {
        _state = BreakerState.Open;
        _openedAt = _timeProvider.GetUtcNow();
        _consecutiveFailures = 0;
        _trialInFlight = false;
        _generation++;
    }

    private void CloseLocked()
    {
        _state = BreakerState.Closed;
        _consecutiveFailures = 0;
        _trialInFlight = false;
        _generation++;
    }
}
