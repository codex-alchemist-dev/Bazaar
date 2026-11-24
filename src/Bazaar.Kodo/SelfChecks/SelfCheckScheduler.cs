namespace Bazaar.Kodo.SelfChecks;

/// <summary>The saved state between runs: the last due instant handled for each definition.</summary>
public sealed record SelfCheckScheduleState(IReadOnlyDictionary<string, DateTimeOffset> LastHandled)
{
    /// <summary>No slot handled yet. A definition seen for the first time runs its newest past slot once; older slots are not made up.</summary>
    public static SelfCheckScheduleState Empty { get; } = new(new Dictionary<string, DateTimeOffset>(StringComparer.Ordinal));
}

/// <summary>Everything one call to <see cref="SelfCheckScheduler.RunDueAsync"/> did.</summary>
/// <param name="Results">One result per definition that ran, in definition order.</param>
/// <param name="State">The state to save.</param>
/// <param name="Faulted">True when something unexpected was caught. The host may log it; it must not stop.</param>
/// <param name="Busy">True when another run was in progress, so this call did nothing.</param>
public sealed record SelfCheckBatch(IReadOnlyList<SelfCheckResult> Results, SelfCheckScheduleState State, bool Faulted, bool Busy);

/// <summary>
/// Runs due self-checks and never throws into the host (doc 46 section 17.6). Each due check runs once per call, however many slots were
/// missed; the newest due slot is closed and the older ones are not made up. A runner that throws, hangs, is missing or reports
/// nothing becomes a skipped, timed-out or faulted result, which is amber and never green. Only the caller's own cancellation
/// propagates. Time comes from the <see cref="TimeProvider"/>.
/// </summary>
public sealed class SelfCheckScheduler
{
    private readonly Dictionary<SelfCheckKind, ISelfCheckRunner> _runners;
    private readonly SelfCheckHistory _history;
    private readonly TimeProvider _time;
    private int _running;

    /// <summary>Creates a scheduler. When two runners claim one kind, the first one wins.</summary>
    public SelfCheckScheduler(IEnumerable<ISelfCheckRunner> runners, SelfCheckHistory history, TimeProvider time)
    {
        ArgumentNullException.ThrowIfNull(runners);
        ArgumentNullException.ThrowIfNull(history);
        ArgumentNullException.ThrowIfNull(time);
        Dictionary<SelfCheckKind, ISelfCheckRunner> map = [];
        foreach (ISelfCheckRunner runner in runners)
        {
            if (runner is not null)
            {
                map.TryAdd(runner.Kind, runner);
            }
        }

        _runners = map;
        _history = history;
        _time = time;
    }

    /// <summary>Runs every enabled definition that is due and records the results in the history.</summary>
    public async Task<SelfCheckBatch> RunDueAsync(IReadOnlyList<SelfCheckDefinition> definitions, SelfCheckScheduleState state, CancellationToken cancellationToken)
    {
        SelfCheckScheduleState current = state ?? SelfCheckScheduleState.Empty;
        if (Interlocked.CompareExchange(ref _running, 1, 0) != 0)
        {
            return new SelfCheckBatch([], current, false, true);
        }

        List<SelfCheckResult> results = [];
        Dictionary<string, DateTimeOffset> last = [];
        bool faulted = false;
        try
        {
            if (current.LastHandled is not null)
            {
                foreach (KeyValuePair<string, DateTimeOffset> pair in current.LastHandled)
                {
                    last[pair.Key] = pair.Value;
                }
            }

            foreach (SelfCheckDefinition definition in definitions ?? [])
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (definition is null || !definition.Enabled)
                {
                    continue;
                }

                try
                {
                    DateTimeOffset now = _time.GetUtcNow();
                    DateTimeOffset? newest = definition.Schedule.PreviousDue(now);
                    if (newest is null)
                    {
                        continue;
                    }

                    // First sight runs the newest slot at or before now once; older ones are not made up.
                    if (last.TryGetValue(definition.Id, out DateTimeOffset handled) && newest.Value <= handled)
                    {
                        continue;
                    }

                    DateTimeOffset slot = newest.Value;
                    SelfCheckResult result = await RunOneAsync(definition, slot, cancellationToken).ConfigureAwait(false);
                    _history.Add(result);
                    results.Add(result);
                    last[definition.Id] = slot;
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception)
                {
                    faulted = true;
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            faulted = true;
        }
        finally
        {
            Volatile.Write(ref _running, 0);
        }

        return new SelfCheckBatch(results, new SelfCheckScheduleState(last), faulted, false);
    }

    private async Task<SelfCheckResult> RunOneAsync(SelfCheckDefinition definition, DateTimeOffset slot, CancellationToken cancellationToken)
    {
        DateTimeOffset started = _time.GetUtcNow();
        if (!_runners.TryGetValue(definition.Kind, out ISelfCheckRunner? runner))
        {
            return SelfCheckResult.Skipped(definition, slot, started, SelfCheckCauses.NoRunner);
        }

        using CancellationTokenSource linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        using CancellationTokenSource delayStop = new();
        Task<SelfCheckRunnerResult> run;
        try
        {
            run = runner.RunAsync(new SelfCheckContext(definition.Id, definition.Kind, slot, started), linked.Token);
        }
        catch (Exception)
        {
            return SelfCheckResult.Faulted(definition, slot, started, _time.GetUtcNow());
        }

        Task delay = Task.Delay(definition.Timeout, _time, delayStop.Token);
        Task finished = await Task.WhenAny(run, delay).ConfigureAwait(false);
        if (finished != run)
        {
            await linked.CancelAsync().ConfigureAwait(false);
            _ = run.ContinueWith(static t => _ = t.Exception, CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
            cancellationToken.ThrowIfCancellationRequested();
            return SelfCheckResult.TimedOut(definition, slot, started, _time.GetUtcNow());
        }

        await delayStop.CancelAsync().ConfigureAwait(false);
        try
        {
            SelfCheckRunnerResult answer = await run.ConfigureAwait(false);
            return SelfCheckResult.FromRunner(definition, slot, started, _time.GetUtcNow(), answer);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            return SelfCheckResult.Faulted(definition, slot, started, _time.GetUtcNow());
        }
    }
}
