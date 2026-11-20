using Microsoft.Extensions.Logging;

namespace Bazaar.Server.Council.Service;

/// <summary>
/// Install settings for the deadline sweeper. Defaults are the sweeper's own: they are not council rules, so they are not
/// part of <see cref="CouncilOptions"/>.
/// </summary>
public sealed record CouncilSweepOptions
{
    /// <summary>Votes examined per pass, for deadlines and for decision recovery each. At least 1.</summary>
    public int BatchSize { get; init; } = 50;

    /// <summary>Time between two passes of the background worker. Must be positive.</summary>
    public TimeSpan Interval { get; init; } = TimeSpan.FromMinutes(1);

    /// <summary>
    /// How far back a pass looks for decided votes whose decision record may be missing (a crash after the decided
    /// transition). Zero or negative looks at nothing.
    /// </summary>
    public TimeSpan DecisionRecoveryLookback { get; init; } = TimeSpan.FromHours(1);

    /// <summary>Checks the values. Throws when any is out of range.</summary>
    /// <exception cref="ArgumentOutOfRangeException">A value is out of range.</exception>
    public void Validate()
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(BatchSize, 1);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(Interval, TimeSpan.Zero);
    }
}

/// <summary>Counts from one sweeper pass.</summary>
/// <param name="DeadlinesProcessed">Votes whose deadline processing finished without error.</param>
/// <param name="DecisionsRedriven">Decided votes whose decision record was re-driven without error.</param>
/// <param name="Failed">Votes whose step threw. Each is logged and does not stop the pass.</param>
public sealed record CouncilSweepResult(int DeadlinesProcessed, int DecisionsRedriven, int Failed);

/// <summary>
/// One pass of the Council deadline sweep (docs/design/10-council-and-voting.md, Lifecycle: the deadline with quorum, and
/// Recording: the decision record). A pass finds open votes whose deadline has passed and calls
/// <see cref="ICouncilService.ProcessDeadlineAsync"/> for each. It also finds votes stuck in Revealed (a crash after the reveal
/// transition) through the same call, and re-drives the decision record of recently decided votes through
/// <see cref="ICouncilService.RedriveDecisionAsync"/>. Every step is idempotent, so a pass that repeats work writes nothing new.
/// </summary>
/// <remarks>
/// The sweeper is a plain type with no timer; <see cref="CouncilDeadlineWorker"/> calls <see cref="RunOnceAsync"/> on an interval.
/// Time comes from the injected <see cref="TimeProvider"/>.
/// </remarks>
public sealed partial class CouncilDeadlineSweeper
{
    private readonly ICouncilStore _store;
    private readonly ICouncilService _council;
    private readonly TimeProvider _clock;
    private readonly CouncilSweepOptions _options;
    private readonly ILogger<CouncilDeadlineSweeper> _logger;

    /// <summary>Creates the sweeper. Throws when the options are out of range.</summary>
    public CouncilDeadlineSweeper(
        ICouncilStore store,
        ICouncilService council,
        TimeProvider clock,
        CouncilSweepOptions options,
        ILogger<CouncilDeadlineSweeper> logger)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _council = council ?? throw new ArgumentNullException(nameof(council));
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _options.Validate();
    }

    /// <summary>
    /// Runs one pass: deadlines first, then decision recovery. A vote that throws is counted and logged, and the pass goes on.
    /// Cancellation stops the pass between votes and throws <see cref="OperationCanceledException"/>.
    /// </summary>
    public async Task<CouncilSweepResult> RunOnceAsync(CancellationToken ct)
    {
        var now = _clock.GetUtcNow();
        var deadlines = 0;
        var redriven = 0;
        var failed = 0;

        foreach (var voteId in await _store.ListVotesDueAsync(now, _options.BatchSize, ct))
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                await _council.ProcessDeadlineAsync(voteId, ct);
                deadlines++;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                failed++;
                LogDeadlineStepFailed(_logger, ex, voteId);
            }
        }

        if (_options.DecisionRecoveryLookback > TimeSpan.Zero)
        {
            var since = now - _options.DecisionRecoveryLookback;
            foreach (var voteId in await _store.ListDecidedSinceAsync(since, _options.BatchSize, ct))
            {
                ct.ThrowIfCancellationRequested();
                try
                {
                    await _council.RedriveDecisionAsync(voteId, ct);
                    redriven++;
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    failed++;
                    LogDecisionRecoveryFailed(_logger, ex, voteId);
                }
            }
        }

        return new CouncilSweepResult(deadlines, redriven, failed);
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Council deadline step failed for vote {VoteId}.")]
    private static partial void LogDeadlineStepFailed(ILogger logger, Exception ex, Guid voteId);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Council decision recovery failed for vote {VoteId}.")]
    private static partial void LogDecisionRecoveryFailed(ILogger logger, Exception ex, Guid voteId);
}
