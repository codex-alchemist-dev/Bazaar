using Bazaar.Contracts.Telemetry;
using Bazaar.Diagnostics.Consent;

namespace Bazaar.Client.Diagnostics;

/// <summary>
/// The wire seam for <c>crash.submit</c>. The host binds it to the authenticated connection (doc 43 section 5.2). It returns
/// the server's ack, or null when no ack came back (a transient failure). It throws <see cref="CrashRefusedException"/> when
/// the server refuses the report as a bad request. Any other exception is transient.
/// </summary>
public interface ICrashTransport
{
    /// <summary>Sends one report and waits for its ack.</summary>
    Task<CrashAck?> SubmitAsync(CrashSubmit submit, CancellationToken cancellationToken);
}

/// <summary>Thrown by a transport when the server refuses a report as a bad request. The uploader treats it as permanent.</summary>
public sealed class CrashRefusedException : Exception
{
    /// <summary>Creates the exception with no message.</summary>
    public CrashRefusedException()
    {
    }

    /// <summary>Creates the exception with the server's reason.</summary>
    /// <param name="message">The refusal reason, for logs. It is not sent anywhere.</param>
    public CrashRefusedException(string message)
        : base(message)
    {
    }

    /// <summary>Creates the exception with the server's reason and an inner exception.</summary>
    public CrashRefusedException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>
/// The upload limits. Doc 43 sets none of these; the defaults are this module's choice and the host may change them.
/// </summary>
/// <param name="MaxAttempts">Failed sends before a report is dropped.</param>
/// <param name="MaxAge">Reports older than this are dropped without a send.</param>
/// <param name="MaxPerMinute">Sends allowed in any one-minute window.</param>
/// <param name="BaseDelay">The first retry delay. It doubles with each failure.</param>
/// <param name="MaxDelay">The cap on any retry delay.</param>
/// <param name="IdlePoll">How long the run loop waits when nothing is scheduled.</param>
public sealed record CrashUploadOptions(
    int MaxAttempts,
    TimeSpan MaxAge,
    int MaxPerMinute,
    TimeSpan BaseDelay,
    TimeSpan MaxDelay,
    TimeSpan IdlePoll)
{
    /// <summary>8 attempts, 14 days, 10 sends a minute, 30 s base, 1 h cap, 1 min idle poll.</summary>
    public static CrashUploadOptions Default { get; } = new(
        8,
        TimeSpan.FromDays(14),
        10,
        TimeSpan.FromSeconds(30),
        TimeSpan.FromHours(1),
        TimeSpan.FromMinutes(1));
}

/// <summary>What one pass of the uploader did. Counts are per queued report.</summary>
/// <param name="Sent">Reports acked and deleted.</param>
/// <param name="Retried">Reports whose send failed transiently and will be retried.</param>
/// <param name="Dropped">Reports deleted without an ack, after the age or attempt limit.</param>
/// <param name="Quarantined">Reports the server refused, or that failed validation. Kept for the owner.</param>
/// <param name="Held">Reports whose redaction did not pass. Never uploaded.</param>
/// <param name="AwaitingApproval">Reports in ask mode that the person has not approved.</param>
/// <param name="Waiting">Reports still inside their retry delay.</param>
/// <param name="RateLimited">True when the pass stopped early at the per-minute limit.</param>
public sealed record CrashUploadSummary(
    int Sent,
    int Retried,
    int Dropped,
    int Quarantined,
    int Held,
    int AwaitingApproval,
    int Waiting,
    bool RateLimited)
{
    /// <summary>A pass that did nothing.</summary>
    public static CrashUploadSummary Empty { get; } = new(0, 0, 0, 0, 0, 0, 0, false);
}

/// <summary>
/// Drains the report queue through <see cref="ICrashTransport"/> (doc 43 sections 5.2 and 7.1). Consent is checked on every
/// pass: nothing is sent in off mode, and in ask mode only reports the person approved with <see cref="Approve"/> are sent.
/// A report is deleted only after its ack, or when it is dropped by the age or attempt limit. A refused report is quarantined.
/// </summary>
public sealed class CrashUploader
{
    private const int _maxBackoffExponent = 30;
    private const string _passResidual = CrashReportBuilder.ResidualPass;
    private static readonly TimeSpan _rateWindow = TimeSpan.FromMinutes(1);
    private static readonly TimeSpan _minimumWake = TimeSpan.FromSeconds(1);

    private readonly CrashReportQueue _queue;
    private readonly ICrashTransport _transport;
    private readonly Func<ConsentSettings> _consent;
    private readonly TimeProvider _time;
    private readonly CrashUploadOptions _options;
    private readonly Func<TimeSpan, CancellationToken, Task> _delay;
    private readonly Func<double> _nextUnit;
    private int _passing;
    private readonly object _state = new();
    private readonly HashSet<Guid> _approved = [];
    private readonly Dictionary<Guid, RetryState> _retries = [];
    private readonly Queue<DateTimeOffset> _sentTimes = new();

    /// <summary>Creates the uploader.</summary>
    /// <param name="queue">The report queue to drain.</param>
    /// <param name="transport">The send seam.</param>
    /// <param name="consent">Reads the company's consent settings. Called on every pass.</param>
    /// <param name="time">The clock for ages, retry times and the rate window.</param>
    /// <param name="options">The upload limits.</param>
    /// <param name="delay">Waits between passes in <see cref="RunAsync"/>. Null uses <see cref="Task.Delay(TimeSpan, CancellationToken)"/>.</param>
    /// <param name="nextUnit">A source of values in [0, 1) for the retry jitter. Null uses <see cref="Random.Shared"/>.</param>
    public CrashUploader(
        CrashReportQueue queue,
        ICrashTransport transport,
        Func<ConsentSettings> consent,
        TimeProvider time,
        CrashUploadOptions options,
        Func<TimeSpan, CancellationToken, Task>? delay = null,
        Func<double>? nextUnit = null)
    {
        ArgumentNullException.ThrowIfNull(queue);
        ArgumentNullException.ThrowIfNull(transport);
        ArgumentNullException.ThrowIfNull(consent);
        ArgumentNullException.ThrowIfNull(time);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentOutOfRangeException.ThrowIfLessThan(options.MaxAttempts, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(options.MaxPerMinute, 1);

        _queue = queue;
        _transport = transport;
        _consent = consent;
        _time = time;
        _options = options;
        _delay = delay ?? ((duration, token) => Task.Delay(duration, token));
        _nextUnit = nextUnit ?? (() => Random.Shared.NextDouble());
    }

    /// <summary>Records the person's approval of one report in ask mode. The approval lasts until the report is acked or dropped.</summary>
    public void Approve(Guid reportId)
    {
        lock (_state)
        {
            _approved.Add(reportId);
        }
    }

    /// <summary>
    /// Runs one pass over the queue, oldest first. Returns at once with nothing done when consent is off, or when the
    /// consent settings cannot be read (fails closed). Passes do not overlap: a call made during a pass returns at once.
    /// </summary>
    public async Task<CrashUploadSummary> DrainOnceAsync(CancellationToken cancellationToken)
    {
        if (Interlocked.CompareExchange(ref _passing, 1, 0) != 0)
        {
            return CrashUploadSummary.Empty;
        }

        try
        {
            var action = ReadAction();
            if (action == ConsentAction.SendNothing)
            {
                return CrashUploadSummary.Empty;
            }

            var summary = CrashUploadSummary.Empty;
            foreach (var item in _queue.List())
            {
                cancellationToken.ThrowIfCancellationRequested();
                var outcome = await UploadOneAsync(item, action, cancellationToken).ConfigureAwait(false);
                summary = Tally(summary, outcome);
                if (outcome == CrashUploadOutcome.RateLimited)
                {
                    break;
                }
            }

            return summary;
        }
        finally
        {
            Volatile.Write(ref _passing, 0);
        }
    }

    /// <summary>
    /// Runs passes until cancelled, waiting between them for the next retry, the next rate-limit slot or the idle poll,
    /// whichever comes first. A failed pass is logged by the host's own handlers and tried again on the next wake.
    /// </summary>
    public async Task RunAsync(CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                try
                {
                    await DrainOnceAsync(cancellationToken).ConfigureAwait(false);
                }
                catch (Exception) when (!cancellationToken.IsCancellationRequested)
                {
                    // The next wake retries the pass; the queue is untouched by a failed read.
                }

                await _delay(NextWake(_time.GetUtcNow()), cancellationToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Cancelled by the host.
        }
    }

    private async Task<CrashUploadOutcome> UploadOneAsync(QueuedReport item, ConsentAction action, CancellationToken cancellationToken)
    {
        var reportId = item.ReportId;
        var report = item.Report;
        var now = _time.GetUtcNow();

        if (now - report.CreatedAt > _options.MaxAge)
        {
            _queue.Remove(reportId);
            Forget(reportId);
            return CrashUploadOutcome.Dropped;
        }

        if (CrashReportValidation.Validate(report).Count > 0)
        {
            _queue.Quarantine(reportId);
            Forget(reportId);
            return CrashUploadOutcome.Quarantined;
        }

        if (!string.Equals(report.Redaction.ResidualScan, _passResidual, StringComparison.Ordinal))
        {
            return CrashUploadOutcome.Held;
        }

        if (action == ConsentAction.PromptPerson && !IsApproved(reportId))
        {
            return CrashUploadOutcome.AwaitingApproval;
        }

        if (IsWaiting(reportId, now))
        {
            return CrashUploadOutcome.Waiting;
        }

        if (!TryTakeSlot(now))
        {
            return CrashUploadOutcome.RateLimited;
        }

        CrashAck? ack = null;
        try
        {
            ack = await _transport.SubmitAsync(new CrashSubmit(report), cancellationToken).ConfigureAwait(false);
        }
        catch (CrashRefusedException)
        {
            _queue.Quarantine(reportId);
            Forget(reportId);
            return CrashUploadOutcome.Quarantined;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            // Transient: the report stays queued and is retried after a delay.
        }

        if (ack is not null && ack.ReportId == reportId)
        {
            _queue.Remove(reportId);
            Forget(reportId);
            return CrashUploadOutcome.Sent;
        }

        return RecordFailure(reportId, now);
    }

    private CrashUploadOutcome RecordFailure(Guid reportId, DateTimeOffset now)
    {
        lock (_state)
        {
            var attempts = (_retries.TryGetValue(reportId, out var previous) ? previous.Attempts : 0) + 1;
            if (attempts >= _options.MaxAttempts)
            {
                _retries.Remove(reportId);
                _approved.Remove(reportId);
                _queue.Remove(reportId);
                return CrashUploadOutcome.Dropped;
            }

            _retries[reportId] = new RetryState(attempts, now + BackoffFor(attempts));
            return CrashUploadOutcome.Retried;
        }
    }

    /// <summary>Capped exponential backoff with jitter of plus or minus 20 percent. The cap applies after the jitter.</summary>
    private TimeSpan BackoffFor(int attempts)
    {
        var exponent = Math.Min(attempts - 1, _maxBackoffExponent);
        var cap = _options.MaxDelay.TotalSeconds;
        var exponential = Math.Min(_options.BaseDelay.TotalSeconds * Math.Pow(2, exponent), cap);
        var jitter = 0.8 + (0.4 * _nextUnit());
        return TimeSpan.FromSeconds(Math.Min(exponential * jitter, cap));
    }

    private bool IsApproved(Guid reportId)
    {
        lock (_state)
        {
            return _approved.Contains(reportId);
        }
    }

    private bool IsWaiting(Guid reportId, DateTimeOffset now)
    {
        lock (_state)
        {
            return _retries.TryGetValue(reportId, out var retry) && now < retry.NextAttemptAt;
        }
    }

    /// <summary>Takes one send from the sliding one-minute window, or returns false when the window is full.</summary>
    private bool TryTakeSlot(DateTimeOffset now)
    {
        lock (_state)
        {
            while (_sentTimes.Count > 0 && now - _sentTimes.Peek() >= _rateWindow)
            {
                _sentTimes.Dequeue();
            }

            if (_sentTimes.Count >= _options.MaxPerMinute)
            {
                return false;
            }

            _sentTimes.Enqueue(now);
            return true;
        }
    }

    private TimeSpan NextWake(DateTimeOffset now)
    {
        var wake = _options.IdlePoll;
        lock (_state)
        {
            foreach (var retry in _retries.Values)
            {
                if (retry.NextAttemptAt > now && retry.NextAttemptAt - now < wake)
                {
                    wake = retry.NextAttemptAt - now;
                }
            }

            if (_sentTimes.Count >= _options.MaxPerMinute)
            {
                var slot = _sentTimes.Peek() + _rateWindow - now;
                if (slot < wake)
                {
                    wake = slot;
                }
            }
        }

        return wake < _minimumWake ? _minimumWake : wake;
    }

    private void Forget(Guid reportId)
    {
        lock (_state)
        {
            _retries.Remove(reportId);
            _approved.Remove(reportId);
        }
    }

    /// <summary>The consent action for this pass. A settings read that throws counts as off.</summary>
    private ConsentAction ReadAction()
    {
        try
        {
            return ConsentDecision.Decide(_consent(), crashReportExists: true);
        }
        catch (Exception)
        {
            return ConsentAction.SendNothing;
        }
    }

    private static CrashUploadSummary Tally(CrashUploadSummary summary, CrashUploadOutcome outcome) => outcome switch
    {
        CrashUploadOutcome.Sent => summary with { Sent = summary.Sent + 1 },
        CrashUploadOutcome.Retried => summary with { Retried = summary.Retried + 1 },
        CrashUploadOutcome.Dropped => summary with { Dropped = summary.Dropped + 1 },
        CrashUploadOutcome.Quarantined => summary with { Quarantined = summary.Quarantined + 1 },
        CrashUploadOutcome.Held => summary with { Held = summary.Held + 1 },
        CrashUploadOutcome.AwaitingApproval => summary with { AwaitingApproval = summary.AwaitingApproval + 1 },
        CrashUploadOutcome.Waiting => summary with { Waiting = summary.Waiting + 1 },
        CrashUploadOutcome.RateLimited => summary with { RateLimited = true },
        _ => summary,
    };

    private enum CrashUploadOutcome
    {
        Sent,
        Retried,
        Dropped,
        Quarantined,
        Held,
        AwaitingApproval,
        Waiting,
        RateLimited,
    }

    private readonly record struct RetryState(int Attempts, DateTimeOffset NextAttemptAt);
}
