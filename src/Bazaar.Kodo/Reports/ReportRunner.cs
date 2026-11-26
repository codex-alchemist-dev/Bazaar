using System.Text.Json;
using Bazaar.Kodo.Export;
using Bazaar.Kodo.Journal;

namespace Bazaar.Kodo.Reports;

/// <summary>What happened to one sink during a run.</summary>
public enum ReportSinkOutcome
{
    Accepted,
    Rejected,
    Failed,
    TimedOut,
}

/// <summary>What happened to one slot.</summary>
public enum ReportRunOutcome
{
    /// <summary>The report was built and at least one sink accepted it.</summary>
    Delivered,

    /// <summary>The report was built and no sink accepted it (there may have been none). The slot is not retried.</summary>
    NotDelivered,

    /// <summary>The input source failed. The slot is kept and retried on the next run.</summary>
    InputFailed,

    /// <summary>The inputs were not acceptable. The slot is closed, since a retry would fail the same way.</summary>
    BuildFailed,
}

/// <summary>Result for one sink.</summary>
public sealed record ReportSinkResult(string SinkId, ReportSinkOutcome Outcome);

/// <summary>Result for one slot.</summary>
/// <param name="Slot">The due instant.</param>
/// <param name="Outcome">What happened.</param>
/// <param name="Digest">The digest of the built report, or null when none was built.</param>
/// <param name="Sinks">Results for every sink, in order.</param>
/// <param name="Journaled">Whether the run record reached the journal. False leaves the journal chain where it was.</param>
public sealed record ReportRunResult(DateTimeOffset Slot, ReportRunOutcome Outcome, string? Digest, IReadOnlyList<ReportSinkResult> Sinks, bool Journaled);

/// <summary>Everything one call to <see cref="ReportRunner.RunDueAsync"/> did.</summary>
/// <param name="Runs">One entry per slot that was tried, oldest first.</param>
/// <param name="Skipped">Missed slots left out by the catch-up cap.</param>
/// <param name="WindowTruncated">The saved state was older than the catch-up window.</param>
/// <param name="State">The state to save. Unchanged when nothing was closed.</param>
/// <param name="NextDue">The next due instant, or null when the schedule could not be evaluated.</param>
/// <param name="Faulted">True when something unexpected was caught and nothing was done. The host may log it; it must not stop.</param>
/// <param name="Busy">True when another run was in progress, so this call did nothing.</param>
public sealed record ReportRunBatch(
    IReadOnlyList<ReportRunResult> Runs,
    int Skipped,
    bool WindowTruncated,
    ReportScheduleState State,
    DateTimeOffset? NextDue,
    bool Faulted,
    bool Busy);

/// <summary>What one run writes to the journal. Identifiers, a digest and outcome words only.</summary>
internal sealed record ReportRunJournalPayload(
    int Version,
    string DefinitionId,
    DateTimeOffset Slot,
    string Outcome,
    string? Digest,
    IReadOnlyList<ReportRunJournalSink> Sinks);

internal sealed record ReportRunJournalSink(string Id, string Outcome);

/// <summary>
/// Runs scheduled reports and never throws into the host (doc 46 section 16.7). For each due slot it gathers the inputs, builds the
/// report, offers it to every sink with a timeout each, and journals the run with the existing hash chain
/// (<see cref="JournalChain.Create"/>). A sink or input source that throws, hangs or declines is recorded and contained. Only the
/// caller's own cancellation propagates. Time comes from the <see cref="TimeProvider"/>.
/// </summary>
/// <remarks>
/// The runner is stateful only in its journal position. Use one runner per journal, and call it from one place; an overlapping
/// call returns a <see cref="ReportRunBatch.Busy"/> batch instead of running twice.
/// </remarks>
public sealed class ReportRunner
{
    /// <summary>The journal record kind written for each run.</summary>
    public const string JournalKind = "report.run";

    private const string _invalidSinkId = "invalid";

    private readonly IReportInputSource _inputs;
    private readonly IReadOnlyList<IReportSink> _sinks;
    private readonly IReportJournal _journal;
    private readonly ReportCatchUpOptions _options;
    private readonly TimeProvider _time;
    private int _running;

    private long _nextSequence;
    private byte[] _previousHash;

    /// <summary>Creates a runner. The options are validated.</summary>
    /// <param name="inputs">Gathers the data for a report.</param>
    /// <param name="sinks">Where reports go. May be empty: the run is then journalled as not delivered.</param>
    /// <param name="journal">Where run records go.</param>
    /// <param name="options">Catch-up and sink limits.</param>
    /// <param name="timeProvider">The clock.</param>
    /// <param name="nextSequence">The sequence of the next journal record, when the chain already has records.</param>
    /// <param name="previousHash">The hash of the last journal record, or null (the genesis hash) for a new chain.</param>
    public ReportRunner(
        IReportInputSource inputs,
        IEnumerable<IReportSink> sinks,
        IReportJournal journal,
        ReportCatchUpOptions options,
        TimeProvider timeProvider,
        long nextSequence = 0,
        byte[]? previousHash = null)
    {
        ArgumentNullException.ThrowIfNull(inputs);
        ArgumentNullException.ThrowIfNull(sinks);
        ArgumentNullException.ThrowIfNull(journal);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(timeProvider);
        options.Validate();
        ArgumentOutOfRangeException.ThrowIfNegative(nextSequence);
        if (previousHash is not null && previousHash.Length != JournalChain.HashLength)
        {
            throw new ArgumentException($"The previous hash must be {JournalChain.HashLength} bytes.", nameof(previousHash));
        }

        _inputs = inputs;
        _sinks = [.. sinks];
        _journal = journal;
        _options = options;
        _time = timeProvider;
        _nextSequence = nextSequence;
        _previousHash = previousHash is null ? JournalChain.GenesisHash() : [.. previousHash];
    }

    /// <summary>The sequence the next journal record will have.</summary>
    public long NextSequence => _nextSequence;

    /// <summary>The hash of the last journal record this runner wrote (or was given).</summary>
    public byte[] PreviousHash => [.. _previousHash];

    /// <summary>
    /// Builds and delivers every report that is due now, within the catch-up bounds. Never throws for a problem in the inputs, a
    /// sink or the journal. It throws <see cref="OperationCanceledException"/> only when <paramref name="cancellationToken"/> is cancelled.
    /// </summary>
    public async Task<ReportRunBatch> RunDueAsync(ReportDefinition definition, ReportScheduleState state, CancellationToken cancellationToken)
    {
        if (definition is null || state is null)
        {
            return Fault(state ?? ReportScheduleState.Armed(_time.GetUtcNow()));
        }

        if (Interlocked.CompareExchange(ref _running, 1, 0) != 0)
        {
            return new ReportRunBatch([], 0, false, state, null, false, true);
        }

        try
        {
            return await RunLockedAsync(definition, state, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            return Fault(state);
        }
        finally
        {
            Volatile.Write(ref _running, 0);
        }
    }

    private static ReportRunBatch Fault(ReportScheduleState state) => new([], 0, false, state, null, true, false);

    private async Task<ReportRunBatch> RunLockedAsync(ReportDefinition definition, ReportScheduleState state, CancellationToken cancellationToken)
    {
        DateTimeOffset now = _time.GetUtcNow();
        ReportDueSet due = ReportScheduler.Evaluate(definition.Schedule, state, now, _options);

        List<ReportRunResult> runs = [];
        ReportScheduleState current = state;
        foreach (DateTimeOffset slot in due.Due)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ReportRunResult result = await RunSlotAsync(definition, slot, cancellationToken);
            runs.Add(result);

            // An input failure keeps the slot for the next run, and nothing after it is built, so the order of reports holds.
            if (result.Outcome == ReportRunOutcome.InputFailed)
            {
                break;
            }

            current = new ReportScheduleState(slot);
        }

        // Slots dropped by the cap lie before the first built slot, so closing the last built slot closes them too.
        return new ReportRunBatch(runs.AsReadOnly(), due.Skipped, due.WindowTruncated, current, due.NextDue, false, false);
    }

    private async Task<ReportRunResult> RunSlotAsync(ReportDefinition definition, DateTimeOffset slot, CancellationToken cancellationToken)
    {
        DateTimeOffset start;
        ReportInputs inputs;
        try
        {
            start = definition.Schedule.PreviousDue(slot);
            inputs = await _inputs.GetAsync(definition, start, slot, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            return await JournalAsync(definition, slot, ReportRunOutcome.InputFailed, null, [], cancellationToken);
        }

        HealthReport report;
        try
        {
            report = HealthReportBuilder.Build(definition, start, slot, inputs, _time.GetUtcNow());
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
        {
            return await JournalAsync(definition, slot, ReportRunOutcome.BuildFailed, null, [], cancellationToken);
        }

        string digest = HealthReportCodec.Digest(report);
        List<ReportSinkResult> results = [];
        foreach (IReportSink sink in _sinks)
        {
            cancellationToken.ThrowIfCancellationRequested();
            results.Add(new ReportSinkResult(SafeId(sink), await PublishOneAsync(report, sink, cancellationToken)));
        }

        ReportRunOutcome outcome = results.Any(r => r.Outcome == ReportSinkOutcome.Accepted) ? ReportRunOutcome.Delivered : ReportRunOutcome.NotDelivered;
        return await JournalAsync(definition, slot, outcome, digest, results, cancellationToken);
    }

    private async Task<ReportSinkOutcome> PublishOneAsync(HealthReport report, IReportSink sink, CancellationToken cancellationToken)
    {
        using CancellationTokenSource timeout = new(_options.SinkTimeout, _time);
        using CancellationTokenSource linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeout.Token);
        try
        {
            bool accepted = await sink.PublishAsync(report, linked.Token);
            return accepted ? ReportSinkOutcome.Accepted : ReportSinkOutcome.Rejected;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException) when (timeout.IsCancellationRequested)
        {
            return ReportSinkOutcome.TimedOut;
        }
        catch (Exception)
        {
            return ReportSinkOutcome.Failed;
        }
    }

    private async Task<ReportRunResult> JournalAsync(
        ReportDefinition definition,
        DateTimeOffset slot,
        ReportRunOutcome outcome,
        string? digest,
        IReadOnlyList<ReportSinkResult> sinks,
        CancellationToken cancellationToken)
    {
        ReportRunJournalPayload payload = new(
            1,
            definition.Id,
            slot,
            outcome.ToString().ToLowerInvariant(),
            digest,
            [.. sinks.Select(s => new ReportRunJournalSink(s.SinkId, s.Outcome.ToString().ToLowerInvariant()))]);

        bool journaled = false;
        try
        {
            byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(payload, ReportJsonContext.Default.ReportRunJournalPayload);
            JournalRecord record = JournalChain.Create(_nextSequence, _time.GetUtcNow(), JournalKind, bytes, _previousHash);
            await _journal.AppendAsync(record, cancellationToken);

            // The chain moves only after the journal took the record, so a failed append leaves no gap.
            _nextSequence++;
            _previousHash = record.Hash;
            journaled = true;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            journaled = false;
        }

        return new ReportRunResult(slot, outcome, digest, sinks, journaled);
    }

    private static string SafeId(IReportSink sink)
    {
        try
        {
            string? id = sink.Id;
            return id is not null && KodoExportPatterns.IdentifierValue().IsMatch(id) ? id : _invalidSinkId;
        }
        catch (Exception)
        {
            return _invalidSinkId;
        }
    }
}
