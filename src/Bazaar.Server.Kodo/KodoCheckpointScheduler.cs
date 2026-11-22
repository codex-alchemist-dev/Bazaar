using Bazaar.Kodo.Anchoring;
using Bazaar.Kodo.Journal;
using Microsoft.Extensions.Logging;

namespace Bazaar.Server.Kodo;

/// <summary>
/// Cuts checkpoints on the stored chain and anchors them (doc 46 section 6.5.2, PROPOSED). Each pass asks <see cref="AnchorPlanner"/>
/// whether the stored journal has grown enough (records) or aged enough (interval) since the last checkpoint, cuts one window, then
/// hands the checkpoints no sink has accepted to <see cref="AnchorPublisher"/>, one anchor per sink. A checkpoint counts as anchored
/// only when a sink accepted. Fail safe: a store or sink problem is logged and retried after the retry wait and never reaches the
/// host or the journal; only the caller's own cancellation is thrown.
/// </summary>
internal sealed partial class KodoCheckpointScheduler
{
    /// <summary>Unanchored checkpoints tried per pass.</summary>
    internal const int AnchorsPerPass = 10;

    private readonly IKodoJournalStore _journal;
    private readonly IKodoCheckpointStore _checkpoints;
    private readonly IReadOnlyList<IAnchorSink> _sinks;
    private readonly IKodoCheckpointSigner? _signer;
    private readonly KodoCheckpointOptions _options;
    private readonly AnchorPlanner _planner;
    private readonly AnchorPublisher _publisher;
    private readonly TimeProvider _time;
    private readonly ILogger _logger;
    private readonly DateTimeOffset _startedAt;
    private DateTimeOffset? _lastCutFailedAt;
    private DateTimeOffset? _lastAnchorFailedAt;

    public KodoCheckpointScheduler(
        IKodoJournalStore journal,
        IKodoCheckpointStore checkpoints,
        IEnumerable<IAnchorSink> sinks,
        KodoCheckpointOptions options,
        TimeProvider time,
        ILogger logger,
        IKodoCheckpointSigner? signer = null,
        TimeSpan? sinkTimeout = null)
    {
        _journal = journal ?? throw new ArgumentNullException(nameof(journal));
        _checkpoints = checkpoints ?? throw new ArgumentNullException(nameof(checkpoints));
        ArgumentNullException.ThrowIfNull(sinks);
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _time = time ?? throw new ArgumentNullException(nameof(time));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _signer = signer;
        _sinks = [.. sinks];
        if (_sinks.Count == 0)
        {
            _sinks = [new NoOpAnchorSink()];
        }

        if (!options.IsValid)
        {
            throw new ArgumentException("Checkpoint options are out of range.", nameof(options));
        }

        _planner = new AnchorPlanner(options.ToPlanOptions(), time);
        _publisher = new AnchorPublisher(sinkTimeout, time);
        _startedAt = time.GetUtcNow();
    }

    /// <summary>Checkpoints cut since start.</summary>
    public int Cut { get; private set; }

    /// <summary>One pass: cut when due, then anchor what is unanchored. Never throws except on the caller's cancellation.</summary>
    public async Task RunPassAsync(CancellationToken cancellationToken)
    {
        try
        {
            await CutIfDueAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception error) when (error is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            _lastCutFailedAt = _time.GetUtcNow();
            LogCutFailed(_logger, error);
        }

        try
        {
            await AnchorPendingAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception error) when (error is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            _lastAnchorFailedAt = _time.GetUtcNow();
            LogAnchorFailed(_logger, error);
        }
    }

    private async Task CutIfDueAsync(CancellationToken cancellationToken)
    {
        JournalRecord? head = await _journal.GetHeadAsync(cancellationToken).ConfigureAwait(false);
        if (head is null)
        {
            return;
        }

        KodoCheckpoint? latest = await _checkpoints.GetLatestCheckpointAsync(cancellationToken).ConfigureAwait(false);
        long start;
        if (latest is not null)
        {
            start = latest.LastSequence + 1;
        }
        else if (await _checkpoints.GetFirstStoredSequenceAsync(cancellationToken).ConfigureAwait(false) is { } first)
        {
            start = first;
        }
        else
        {
            return;
        }

        AnchorProgress progress = new(start, latest?.CutAt ?? _startedAt);
        AnchorPlan plan = _planner.Evaluate(head.Sequence + 1, progress, _lastCutFailedAt);
        if (!plan.ShouldAnchor)
        {
            return;
        }

        int count = (int)Math.Min(head.Sequence + 1 - start, _options.MaxRecords);
        IReadOnlyList<JournalRecord> window = await _journal.ReadRangeAsync(start, count, cancellationToken).ConfigureAwait(false);
        KodoCheckpoint? checkpoint = window.Count > 0 && window[0].Sequence == start ? KodoCheckpoints.Build(window, _time.GetUtcNow()) : null;
        if (checkpoint is null || (latest is not null && !checkpoint.PreviousHead.AsSpan().SequenceEqual(latest.HeadHash)))
        {
            // A hole or a link that does not match the previous checkpoint: never papered over (doc 46 section 7.10).
            _lastCutFailedAt = _time.GetUtcNow();
            LogWindowRejected(_logger, start);
            return;
        }

        checkpoint = await SignAsync(checkpoint, cancellationToken).ConfigureAwait(false);
        if (await _checkpoints.SaveCheckpointAsync(checkpoint, cancellationToken).ConfigureAwait(false))
        {
            Cut++;
        }

        _lastCutFailedAt = null;
    }

    private async Task<KodoCheckpoint> SignAsync(KodoCheckpoint checkpoint, CancellationToken cancellationToken)
    {
        if (_signer is null)
        {
            return checkpoint;
        }

        try
        {
            byte[] signature = await _signer.SignAsync(checkpoint.Digest, cancellationToken).ConfigureAwait(false);
            return signature is { Length: > 0 } && _signer.Id is { Length: >= 1 and <= 128 }
                ? checkpoint with { Signature = signature, SignerId = _signer.Id }
                : checkpoint;
        }
        catch (Exception error) when (error is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            LogSignFailed(_logger, error);
            return checkpoint;
        }
    }

    private async Task AnchorPendingAsync(CancellationToken cancellationToken)
    {
        DateTimeOffset now = _time.GetUtcNow();
        if (_lastAnchorFailedAt is { } failed && now - failed < _options.RetryInterval)
        {
            return;
        }

        IReadOnlyList<KodoCheckpoint> pending = await _checkpoints.ListUnanchoredAsync(AnchorsPerPass, cancellationToken).ConfigureAwait(false);
        foreach (KodoCheckpoint checkpoint in pending)
        {
            bool accepted = false;
            foreach (IAnchorSink sink in _sinks)
            {
                AnchorRecord anchor = KodoCheckpoints.ToAnchor(checkpoint, SafeId(sink), _time.GetUtcNow());
                AnchorPublishReport report = await _publisher.PublishAsync(anchor, [sink], cancellationToken).ConfigureAwait(false);
                if (report.AnyAccepted)
                {
                    await _checkpoints.RecordAnchorAsync(checkpoint.LastSequence, anchor.SinkId, anchor.AnchoredAt, cancellationToken).ConfigureAwait(false);
                    accepted = true;
                }
            }

            if (!accepted)
            {
                // Nothing took it: wait before trying again so a dead sink is not hammered. Later checkpoints wait too, to keep order.
                _lastAnchorFailedAt = _time.GetUtcNow();
                LogNoSinkAccepted(_logger, checkpoint.LastSequence);
                return;
            }
        }

        _lastAnchorFailedAt = null;
    }

    private static string SafeId(IAnchorSink sink)
    {
        try
        {
            return string.IsNullOrEmpty(sink.Id) || sink.Id.Length > 128 ? "unnamed" : sink.Id;
        }
        catch (Exception)
        {
            return "unnamed";
        }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Cutting a Kodo checkpoint failed. It is tried again after the retry wait.")]
    private static partial void LogCutFailed(ILogger logger, Exception error);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Anchoring Kodo checkpoints failed. It is tried again after the retry wait.")]
    private static partial void LogAnchorFailed(ILogger logger, Exception error);

    [LoggerMessage(Level = LogLevel.Error, Message = "The stored journal from record {Start} has a hole or does not link to the previous checkpoint. No checkpoint was cut.")]
    private static partial void LogWindowRejected(ILogger logger, long start);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Signing a Kodo checkpoint failed. It is stored unsigned.")]
    private static partial void LogSignFailed(ILogger logger, Exception error);

    [LoggerMessage(Level = LogLevel.Warning, Message = "No anchor sink accepted the checkpoint ending at record {Sequence}. Retention keeps the journal tail until one does.")]
    private static partial void LogNoSinkAccepted(ILogger logger, long sequence);
}

/// <summary>Checkpoint settings. Off by default; the numbers follow the anchor planner's ranges plus a window cap.</summary>
internal sealed record KodoCheckpointOptions
{
    /// <summary>Most records in one checkpoint window, and the record count that makes a checkpoint due. Range 1 to 100,000.</summary>
    public int MaxRecords { get; init; } = 1000;

    /// <summary>Longest time between checkpoints while there are new records. Range 1 second to 24 hours.</summary>
    public TimeSpan Interval { get; init; } = TimeSpan.FromMinutes(15);

    /// <summary>Wait after a failed cut or anchor attempt. Range 1 second to 1 hour.</summary>
    public TimeSpan RetryInterval { get; init; } = TimeSpan.FromMinutes(1);

    public bool IsValid => MaxRecords is >= 1 and <= 100_000 && ToPlanOptions().IsValid;

    public AnchorPlanOptions ToPlanOptions() => new() { MaxRecords = MaxRecords, MaxInterval = Interval, RetryInterval = RetryInterval };
}
