using System.Text;
using Bazaar.Contracts.Kodo;
using Bazaar.Kodo.Anchoring;
using Bazaar.Kodo.Api;
using Bazaar.Kodo.Evaluator;
using Bazaar.Kodo.Journal;
using Bazaar.Kodo.Probes;
using Bazaar.Kodo.Probes.Host;
using Bazaar.Kodo.Probes.Self;
using Bazaar.Kodo.Settings;
using Microsoft.Extensions.Logging;

namespace Bazaar.Server.Kodo;

/// <summary>
/// The Kodo engine of the server module (doc 46 section 5.4): one pass of <see cref="RunCycleAsync"/> runs the built-in probes that
/// are due through their bulkheads and breakers, journals what changed, evaluates the state, builds the snapshot and publishes the
/// stream events. It holds no timer; <see cref="KodoMonitorService"/> drives it, and a test drives it with a fake clock.
/// </summary>
/// <remarks>
/// A pass never throws except when the caller cancels. Probe failures are results, journal failures mark the journal not writable,
/// and a publisher failure is logged and dropped, so no part can stop another.
/// </remarks>
internal sealed partial class KodoMonitor : IKodoStatusSource, IDisposable
{
    private const string _dataVolume = "data";
    private static readonly TimeSpan _storeTimeout = TimeSpan.FromSeconds(15);

    private readonly KodoServerOptions _options;
    private readonly TimeProvider _time;
    private readonly IKodoStreamPublisher _publisher;
    private readonly ILogger _logger;
    private readonly KodoJournal _journal;
    private readonly KodoJournalSync? _sync;
    private readonly IKodoJournalStore? _store;
    private readonly KodoStreamWriter _stream = new();
    private readonly SemaphoreSlim _cycle = new(1, 1);
    private readonly Dictionary<string, Bulkhead> _pools = new(StringComparer.Ordinal);
    private readonly List<ProbeSlot> _slots = [];
    private readonly KodoEvaluatorSettings _evaluatorSettings;
    private readonly TimeSpan _probeTimeout;
    private KodoEvaluatorMemory _memory = KodoEvaluator.Initial;
    private KodoState? _shown;
    private DateTimeOffset? _lastHeartbeat;
    private KodoStatusSnapshotDto? _latest;
    private bool _started;

    public KodoMonitor(KodoServerOptions options, IHostReadings host, IKodoStreamPublisher publisher, TimeProvider time, ILogger<KodoMonitor> logger, IKodoJournalStore? store = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(host);
        _options = options;
        _publisher = publisher ?? throw new ArgumentNullException(nameof(publisher));
        _time = time ?? throw new ArgumentNullException(nameof(time));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));

        KodoSettings s = options.Settings;
        _evaluatorSettings = new KodoEvaluatorSettings
        {
            StabilityWindow = TimeSpan.FromSeconds(s.GetInt(KodoSettingKey.StabilityWindowSeconds)),
            FlapLimit = s.GetInt(KodoSettingKey.FlapLimitTransitions),
            FlapWindow = TimeSpan.FromMinutes(s.GetInt(KodoSettingKey.FlapLimitWindowMinutes)),
        };
        _probeTimeout = TimeSpan.FromMilliseconds(s.GetInt(KodoSettingKey.ProbeTimeoutMs));
        _journal = new KodoJournal(options.DataDirectory, options.JournalMemoryRecords, JournalSpillBound.BytesFromMegabytes(s.GetInt(KodoSettingKey.JournalSpillMaxMb)));
        if (store is not null && options.UseJournalStore)
        {
            _store = store;
            _sync = new KodoJournalSync(_journal, store, options, time, logger);
        }

        int failures = s.GetInt(KodoSettingKey.BreakerFailures);
        TimeSpan cooldown = TimeSpan.FromSeconds(s.GetInt(KodoSettingKey.BreakerCooldownSeconds));
        Bulkhead PoolFor(string pool) => _pools.TryGetValue(pool, out Bulkhead? existing)
            ? existing
            : _pools[pool] = new Bulkhead(pool, s.GetInt($"kodo.probe.pool.{pool}.concurrency"), time);

        void Add(string id, KodoCategory category, bool critical, Func<CancellationToken, Task<ProbeResult>> run)
        {
            ProbeCatalogueEntry row = BuiltInProbeCatalogue.Entries.First(entry => id.StartsWith(entry.Id, StringComparison.Ordinal));
            _slots.Add(new ProbeSlot(id, category, critical, row.Cadence, new ProbeRunner(PoolFor(row.Pool), new CircuitBreaker(failures, cooldown, time)), run));
        }

        DiskFreeProbe disk = new(host, _dataVolume);
        MemoryPressureProbe memory = new(host);
        JournalWritableProbe writable = new(new KodoSelfReadings(_journal, _sync));
        SpillAgeProbe spill = new(new KodoSelfReadings(_journal, _sync), timeProvider: time);
        Add(disk.Id, KodoCategory.Host, critical: true, disk.RunAsync);
        Add(MemoryPressureProbe.Id, KodoCategory.Host, critical: false, memory.RunAsync);
        if (options.IncludeClockDrift)
        {
            ClockDriftProbe clock = new(host);
            Add(ClockDriftProbe.Id, KodoCategory.Host, critical: true, clock.RunAsync);
        }

        Add(JournalWritableProbe.Id, KodoCategory.Server, critical: true, writable.RunAsync);
        Add(SpillAgeProbe.Id, KodoCategory.Server, critical: false, spill.RunAsync);
    }

    public bool Enabled => true;

    public KodoStatusSnapshotDto? Latest => Volatile.Read(ref _latest);

    public IReadOnlyList<KodoJournalEntryView> RecentJournal(int limit) =>
        _journal.Recent(Math.Clamp(limit, 1, 200)).Select(ToView).ToArray();

    public KodoJournalProofView? JournalProof(long sequence)
    {
        var found = _journal.Proof(sequence);
        if (found is not { } proof)
        {
            return null;
        }

        return new KodoJournalProofView(
            sequence,
            proof.Proof.TreeSize,
            Hex(proof.RecordHash),
            Hex(proof.Root),
            proof.Proof.Path.Select(Hex).ToArray());
    }

    public async ValueTask<KodoJournalProofView?> JournalProofAsync(long sequence, CancellationToken cancellationToken)
    {
        if (JournalProof(sequence) is { } local)
        {
            return local;
        }

        // The memory window cannot answer (older than the window): ask the store, which holds the whole history.
        if (_store is null || sequence < 0 || sequence >= _journal.NextSequence)
        {
            return null;
        }

        try
        {
            return await _store.ProofAsync(sequence, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception error)
        {
            LogProofStoreFailed(_logger, error);
            return null;
        }
    }

    public async ValueTask<IReadOnlyList<KodoStateChange>> StateHistoryAsync(long? beforeSequence, int limit, CancellationToken cancellationToken)
    {
        IReadOnlyList<KodoStateChange> local = _journal.StateChanges(beforeSequence, limit);
        if (_store is null)
        {
            return local;
        }

        try
        {
            IReadOnlyList<KodoStateChange> stored = await _store.ReadStateChangesAsync(beforeSequence, limit, cancellationToken).ConfigureAwait(false);

            // The store may lag the memory journal (records not yet replayed): the union by sequence is the full history.
            return stored
                .Concat(local)
                .GroupBy(change => change.Sequence)
                .Select(group => group.First())
                .OrderByDescending(change => change.Sequence)
                .Take(limit)
                .ToArray();
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception error)
        {
            LogHistoryStoreFailed(_logger, error);
            return local;
        }
    }

    /// <summary>The ids of the probes this monitor runs, in catalogue order.</summary>
    public IReadOnlyList<string> ProbeIds => _slots.Select(slot => slot.Id).ToArray();

    /// <summary>Runs one pass. Only the caller's own cancellation is thrown.</summary>
    public async Task RunCycleAsync(CancellationToken cancellationToken)
    {
        await _cycle.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await RunPassAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception error)
        {
            LogCycleFailed(_logger, error);
        }
        finally
        {
            _cycle.Release();
        }
    }

    public void Dispose()
    {
        _journal.Dispose();
        foreach (Bulkhead pool in _pools.Values)
        {
            pool.Dispose();
        }

        _cycle.Dispose();
    }

    private async Task RunPassAsync(CancellationToken ct)
    {
        DateTimeOffset now = _time.GetUtcNow();
        List<KodoStreamEventDto> events = [];

        if (!_started)
        {
            _started = true;
            if (_sync is not null)
            {
                // Connect first: with no local chain, the journal continues from the stored head instead of starting a second chain.
                await SyncStepAsync((sync, token) => sync.ConnectAsync(token), ct).ConfigureAwait(false);
            }

            _journal.Append(now, "start", "kodo monitor started");
        }

        ProbeSlot[] due = _slots.Where(slot => slot.LastRun is null || now - slot.LastRun >= slot.Cadence).ToArray();
        await Task.WhenAll(due.Select(slot => RunProbeAsync(slot, now, ct))).ConfigureAwait(false);

        KodoObservation observation = Observe(now);
        if (_memory.Underlying == KodoState.Failing && observation.AllRulesGreen)
        {
            // PROPOSED passive recovery (section 5.4): with no Responder, an all-green sample after failing is reported to the
            // evaluator as one running recovery, so failing can become recovering and then healthy through the stability window.
            observation = observation with { Recovery = RecoveryStatus.Running };
        }

        KodoEvaluation evaluation = KodoEvaluator.Evaluate(_memory, observation, now, _evaluatorSettings);
        _memory = evaluation.Next;

        if (_shown is { } previous && previous != evaluation.State)
        {
            _journal.Append(now, "state", $"state {KodoWireNames.State(previous)} -> {KodoWireNames.State(evaluation.State)}");
            events.Add(_stream.StateChanged(previous, evaluation, now));
        }
        else if (_shown is null && evaluation.State != KodoState.Healthy)
        {
            _journal.Append(now, "state", $"state {KodoWireNames.State(KodoState.Healthy)} -> {KodoWireNames.State(evaluation.State)}");
        }

        _shown = evaluation.State;

        if (_lastHeartbeat is null || now - _lastHeartbeat >= _options.HeartbeatInterval)
        {
            _lastHeartbeat = now;
            events.Add(_stream.Heartbeat(now));
        }

        int score = ScoreOf(observation, evaluation);
        KodoComponentInput[] components = _slots
            .Where(slot => slot.HasResult)
            .Select(slot => new KodoComponentInput(slot.Id, slot.Status, slot.Cause, slot.Since))
            .ToArray();
        byte[]? head = _journal.Head;
        KodoStreamEventDto snapshotEvent = _stream.Snapshot(
            revision => KodoStatusSnapshotBuilder.Build(evaluation, observation, score, components, revision, now, head),
            now);
        Volatile.Write(ref _latest, snapshotEvent.Snapshot);
        WriteSnapshotFile(snapshotEvent.Snapshot);
        if (_sync is not null)
        {
            await SyncStepAsync((sync, token) => sync.SyncAsync(snapshotEvent.Snapshot, token), ct).ConfigureAwait(false);
        }

        foreach (KodoStreamEventDto streamEvent in events)
        {
            await PublishAsync(streamEvent, ct).ConfigureAwait(false);
        }
    }

    // The snapshot file is how the resident TUI, a separate process, reads Kodo (doc 58 section 3). A write failure never stops a pass;
    // it is logged once until a write succeeds again.
    private bool _snapshotFileFailing;

    private void WriteSnapshotFile(KodoStatusSnapshotDto? snapshot)
    {
        if (_options.SnapshotFile is not { Length: > 0 } path || snapshot is null)
        {
            return;
        }

        try
        {
            KodoSnapshotFile.Write(path, snapshot);
            _snapshotFileFailing = false;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            if (!_snapshotFileFailing)
            {
                _snapshotFileFailing = true;
                LogSnapshotFileFailed(_logger, error);
            }
        }
    }

    // The store call never holds a pass longer than the timeout: a hung database leaves the records in the local journal.
    private async Task SyncStepAsync(Func<KodoJournalSync, CancellationToken, Task> step, CancellationToken ct)
    {
        using CancellationTokenSource timeout = new(_storeTimeout, _time);
        using CancellationTokenSource limit = CancellationTokenSource.CreateLinkedTokenSource(ct, timeout.Token);
        try
        {
            await step(_sync!, limit.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            LogSyncTimedOut(_logger);
        }
    }

    private async Task RunProbeAsync(ProbeSlot slot, DateTimeOffset now, CancellationToken ct)
    {
        ProbeResult result;
        try
        {
            result = await slot.Runner.RunAsync(slot.Run, _probeTimeout, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception error)
        {
            result = ProbeResult.FromException(error);
        }

        slot.LastRun = now;
        string? cause = result.Status == ProbeStatus.Green ? null : result.Cause ?? ProbeCauses.Unreadable;
        if (!slot.HasResult || slot.Status != result.Status || slot.Cause != cause)
        {
            string from = slot.HasResult ? KodoWireNames.Probe(slot.Status) : "new";
            _journal.Append(now, "observation", $"probe {slot.Id} {from} -> {KodoWireNames.Probe(result.Status)}" + (cause is null ? string.Empty : $" cause={cause}"));
            if (!slot.HasResult || slot.Status != result.Status)
            {
                slot.Since = now;
            }

            slot.HasResult = true;
            slot.Status = result.Status;
            slot.Cause = cause;
        }
    }

    // Section 8.2 and 9: a critical probe makes the state; a non-critical one counts only after its grace. Unknown is never green.
    private KodoObservation Observe(DateTimeOffset now)
    {
        bool criticalRed = false, criticalUnknown = false, amberBeyond = false, withinGrace = false;
        int redBeyond = 0;
        foreach (ProbeSlot slot in _slots.Where(slot => slot.HasResult && slot.Status != ProbeStatus.Green))
        {
            bool beyond = now - slot.Since >= _options.NonCriticalGrace;
            switch (slot.Status)
            {
                case ProbeStatus.Red when slot.Critical:
                    criticalRed = true;
                    break;
                case ProbeStatus.Unknown when slot.Critical:
                    criticalUnknown = true;
                    break;
                case ProbeStatus.Red when beyond:
                    redBeyond++;
                    break;
                case ProbeStatus.Red:
                    withinGrace = true;
                    break;
                default:
                    // Amber, or unknown on a non-critical probe: an open rule that is not green.
                    amberBeyond |= beyond;
                    withinGrace |= !beyond;
                    break;
            }
        }

        return new KodoObservation
        {
            CriticalRed = criticalRed,
            CriticalUnknown = criticalUnknown,
            NonCriticalRedBeyondGrace = redBeyond,
            NonCriticalAmberBeyondGrace = amberBeyond,
            NonCriticalWithinGrace = withinGrace,
        };
    }

    private int ScoreOf(KodoObservation observation, KodoEvaluation evaluation)
    {
        Dictionary<KodoCategory, int> scores = [];
        foreach (KodoCategory category in Enum.GetValues<KodoCategory>())
        {
            int[] parts = _slots.Where(slot => slot.Category == category && slot.HasResult).Select(slot => ProbeScore(slot.Status)).ToArray();
            scores[category] = parts.Length == 0 ? 100 : (int)Math.Round(parts.Average(), MidpointRounding.AwayFromZero);
        }

        int score = KodoScore.Compute(scores, observation);
        return evaluation.Underlying == KodoState.Failing ? Math.Min(score, KodoScore.CriticalRedCap) : score;
    }

    private static int ProbeScore(ProbeStatus status) => status switch
    {
        ProbeStatus.Green => 100,
        ProbeStatus.Amber => 70,
        ProbeStatus.Unknown => 50,
        _ => 0,
    };

    private async Task PublishAsync(KodoStreamEventDto streamEvent, CancellationToken ct)
    {
        try
        {
            await _publisher.PublishAsync(streamEvent, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception error)
        {
            LogPublishFailed(_logger, error, streamEvent.Type);
        }
    }

    private static KodoJournalEntryView ToView(JournalRecord record) =>
        new(record.Sequence, record.At, record.Kind, Hex(record.Hash), Hex(record.PreviousHash), Encoding.UTF8.GetString(record.Payload));

    private static string Hex(byte[] hash) => Convert.ToHexString(hash).ToLowerInvariant();

    [LoggerMessage(Level = LogLevel.Error, Message = "A Kodo pass failed and will be retried at the next tick.")]
    private static partial void LogCycleFailed(ILogger logger, Exception error);

    [LoggerMessage(Level = LogLevel.Warning, Message = "The Kodo journal store could not answer a state history request; the memory journal answers instead.")]
    private static partial void LogHistoryStoreFailed(ILogger logger, Exception error);

    [LoggerMessage(Level = LogLevel.Warning, Message = "The Kodo snapshot file could not be written; the resident TUI will show Kodo as silent.")]
    private static partial void LogSnapshotFileFailed(ILogger logger, Exception error);

    [LoggerMessage(Level = LogLevel.Warning, Message = "The Kodo journal store could not answer a proof request.")]
    private static partial void LogProofStoreFailed(ILogger logger, Exception error);

    [LoggerMessage(Level = LogLevel.Warning, Message = "The Kodo journal store did not answer in time; the records stay in the local journal.")]
    private static partial void LogSyncTimedOut(ILogger logger);

    [LoggerMessage(Level = LogLevel.Warning, Message = "The Kodo stream publisher failed for a {EventType} event; the event is dropped.")]
    private static partial void LogPublishFailed(ILogger logger, Exception error, string eventType);

    private sealed class ProbeSlot(string id, KodoCategory category, bool critical, TimeSpan cadence, ProbeRunner runner, Func<CancellationToken, Task<ProbeResult>> run)
    {
        public string Id { get; } = id;

        public KodoCategory Category { get; } = category;

        public bool Critical { get; } = critical;

        public TimeSpan Cadence { get; } = cadence;

        public ProbeRunner Runner { get; } = runner;

        public Func<CancellationToken, Task<ProbeResult>> Run { get; } = run;

        public DateTimeOffset? LastRun { get; set; }

        public bool HasResult { get; set; }

        public ProbeStatus Status { get; set; }

        public string? Cause { get; set; }

        public DateTimeOffset Since { get; set; }
    }
}
