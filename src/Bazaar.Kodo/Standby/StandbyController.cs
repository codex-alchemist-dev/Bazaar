namespace Bazaar.Kodo.Standby;

/// <summary>Stable outcome codes for <see cref="StandbyTickResult"/>. Standby "stay" outcomes use <see cref="FailoverReasons"/>.</summary>
public static class StandbyOutcomes
{
    /// <summary>Primary, lease healthy, nothing to do.</summary>
    public const string Idle = "idle";

    /// <summary>Primary renewed its lease.</summary>
    public const string Renewed = "renewed";

    /// <summary>Primary could not renew this time. The lease keeps running down.</summary>
    public const string RenewFailed = "renew_failed";

    /// <summary>The node took the lease and is now primary.</summary>
    public const string Promoted = "promoted";

    /// <summary>The node fenced itself. See the cause.</summary>
    public const string Fenced = "fenced";

    /// <summary>Fewer witnesses granted than the quorum.</summary>
    public const string GrantQuorumMissing = "grant_quorum_missing";

    /// <summary>The journal could not be written, so the change was refused.</summary>
    public const string JournalUnavailable = "journal_unavailable";

    /// <summary>The state could not be saved, so the change was refused.</summary>
    public const string StoreUnavailable = "store_unavailable";

    /// <summary>The granted lease ran out before the promotion could take effect.</summary>
    public const string LeaseExpiredBeforeCommit = "lease_expired_before_commit";

    /// <summary>A bootstrap found a lease already exists, or the node is not a fresh standby.</summary>
    public const string NotBootstrappable = "not_bootstrappable";

    /// <summary>A fenced node waits for a person.</summary>
    public const string WaitingForPerson = "waiting_for_person";
}

/// <summary>The result of one controller step.</summary>
/// <param name="Role">The role after the step.</param>
/// <param name="Epoch">The node's epoch after the step.</param>
/// <param name="Outcome">A code from <see cref="StandbyOutcomes"/> or <see cref="FailoverReasons"/>.</param>
/// <param name="Cause">The cause code when the node fenced itself, otherwise null.</param>
public sealed record StandbyTickResult(StandbyRole Role, long Epoch, string Outcome, string? Cause = null)
{
    /// <summary>True when the step ended in a split brain, so the caller enters incident mode with cause <c>split_brain</c> (doc 46 section 16.3).</summary>
    public bool SplitBrain => Cause == StandbyCauses.SplitBrain;
}

/// <summary>
/// The warm standby controller (doc 46 section 7.6): one per node. It runs the lease, failover, demotion and fencing rules over
/// the witness, journal and state-store interfaces, and reads the clock only through the <see cref="TimeProvider"/>. It is fail-safe:
/// a witness that throws or hangs counts as no answer, a change that cannot be journalled and saved is refused, and fencing itself is
/// never blocked by a journal or store failure. A node that cannot read its saved state fences itself. Safe to call from any thread.
/// </summary>
public sealed class StandbyController : IDisposable
{
    private readonly string _nodeId;
    private readonly string _peerId;
    private readonly WitnessQuorumPolicy _policy;
    private readonly IStandbyWitness[] _witnesses;
    private readonly IStandbyJournal _journal;
    private readonly IStandbyStateStore _store;
    private readonly StandbyOptions _options;
    private readonly TimeProvider _time;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private volatile StandbyState _state;

    /// <summary>Creates a controller that starts as a standby with no epoch. Call <see cref="InitializeAsync"/> before use.</summary>
    /// <param name="nodeId">This node's id.</param>
    /// <param name="peerId">The other node's id, asked about until a lease names the holder.</param>
    /// <param name="policy">The witness set and quorum.</param>
    /// <param name="witnesses">One witness for every id in the policy.</param>
    /// <param name="journal">The journal seam.</param>
    /// <param name="store">The state store seam.</param>
    /// <param name="options">The timings; validated here.</param>
    /// <param name="timeProvider">The clock.</param>
    /// <exception cref="ArgumentException">An id is empty, the witnesses do not match the policy, or an option is out of range.</exception>
    public StandbyController(
        string nodeId,
        string peerId,
        WitnessQuorumPolicy policy,
        IEnumerable<IStandbyWitness> witnesses,
        IStandbyJournal journal,
        IStandbyStateStore store,
        StandbyOptions options,
        TimeProvider timeProvider)
    {
        ArgumentException.ThrowIfNullOrEmpty(nodeId);
        ArgumentException.ThrowIfNullOrEmpty(peerId);
        ArgumentNullException.ThrowIfNull(policy);
        ArgumentNullException.ThrowIfNull(witnesses);
        ArgumentNullException.ThrowIfNull(journal);
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(timeProvider);
        options.Validate();

        _witnesses = witnesses.ToArray();
        if (_witnesses.Length != policy.WitnessIds.Count
            || _witnesses.Any(w => !policy.Contains(w.Id))
            || _witnesses.Select(w => w.Id).Distinct(StringComparer.Ordinal).Count() != _witnesses.Length)
        {
            throw new ArgumentException("There must be exactly one witness for every id in the policy.", nameof(witnesses));
        }

        _nodeId = nodeId;
        _peerId = peerId;
        _policy = policy;
        _journal = journal;
        _store = store;
        _options = options;
        _time = timeProvider;
        _state = new StandbyState
        {
            Version = StandbyState.CurrentVersion,
            NodeId = nodeId,
            Role = StandbyRole.Standby,
            Epoch = 0,
            HighestEpochSeen = 0,
            Lease = null,
            Cause = null,
            At = timeProvider.GetUtcNow().ToUniversalTime(),
        };
    }

    /// <summary>Releases the internal lock.</summary>
    public void Dispose() => _gate.Dispose();

    /// <summary>The node's current state.</summary>
    public StandbyState Current => _state;

    /// <summary>
    /// Loads the saved state. A missing state is a fresh standby. A state that cannot be read, is for another node or is not well
    /// formed fences the node. A saved primary resumes only if its lease is still valid, otherwise it fences itself.
    /// </summary>
    public async Task<StandbyTickResult> InitializeAsync(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            DateTimeOffset now = _time.GetUtcNow();
            StandbyState? loaded;
            try
            {
                loaded = await _store.LoadAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                return await FenceAsync(_state, StandbyCauses.StateUnreadable, _state.HighestEpochSeen, now, cancellationToken).ConfigureAwait(false);
            }

            if (loaded is null)
            {
                return Result(_state, StandbyOutcomes.Idle);
            }

            if (!loaded.IsWellFormed || loaded.NodeId != _nodeId)
            {
                return await FenceAsync(_state, StandbyCauses.StateUnreadable, _state.HighestEpochSeen, now, cancellationToken).ConfigureAwait(false);
            }

            _state = loaded;
            SelfFenceDecision fence = SelfFence.Evaluate(loaded.Role, loaded.Epoch, loaded.Lease, loaded.HighestEpochSeen, now);
            return fence.Fence
                ? await FenceAsync(loaded, StandbyCauses.RestartLeaseLost, loaded.HighestEpochSeen, now, cancellationToken).ConfigureAwait(false)
                : Result(loaded, StandbyOutcomes.Idle);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Runs one step for the node's role: renew or self-fence as primary, watch and maybe fail over as standby.</summary>
    public async Task<StandbyTickResult> TickAsync(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            StandbyState state = _state;
            return state.Role switch
            {
                StandbyRole.Primary => await TickPrimaryAsync(state, cancellationToken).ConfigureAwait(false),
                StandbyRole.Standby => await TickStandbyAsync(state, cancellationToken).ConfigureAwait(false),
                _ => Result(state, StandbyOutcomes.WaitingForPerson),
            };
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Takes the very first lease (epoch 1) on a fresh install. Refused unless the node is a standby that has never seen a lease and
    /// no reporting witness knows one. It still needs a witness quorum to grant.
    /// </summary>
    public async Task<StandbyTickResult> BootstrapAsync(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            StandbyState state = _state;
            if (state.Role != StandbyRole.Standby || state.Lease is not null || state.HighestEpochSeen != 0)
            {
                return Result(state, StandbyOutcomes.NotBootstrappable);
            }

            DateTimeOffset now = _time.GetUtcNow();
            IReadOnlyList<WitnessReport> reports = await ObserveAllAsync(_peerId, cancellationToken).ConfigureAwait(false);
            if (reports.Any(r => r.HighestLease is not null))
            {
                return Result(state, StandbyOutcomes.NotBootstrappable);
            }

            return await TryAcquireAsync(state, new FencingToken(1), now, StandbyCauses.Bootstrap, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Checks the fencing token on a write or control action. Only the current epoch of a live primary is admitted. A token from a
    /// newer epoch fences this node, and a primary whose lease has run out fences itself rather than admit a write.
    /// </summary>
    public async Task<FencingVerdict> AdmitAsync(FencingToken presented, CancellationToken cancellationToken)
    {
        StandbyState state = _state;
        if (state.Role != StandbyRole.Primary)
        {
            return FencingVerdict.NotPrimary;
        }

        FencingVerdict verdict = FencingToken.Check(presented, new FencingToken(state.Epoch));
        DateTimeOffset now = _time.GetUtcNow();
        if (verdict == FencingVerdict.Stale)
        {
            await TryJournalAsync(state, StandbyJournalKinds.StaleRejected, now, cancellationToken).ConfigureAwait(false);
            return verdict;
        }

        if (verdict == FencingVerdict.Ahead)
        {
            await FenceIfPrimaryAsync(StandbyCauses.Superseded, presented.Epoch, cancellationToken).ConfigureAwait(false);
            return verdict;
        }

        if (SelfFence.Evaluate(state.Role, state.Epoch, state.Lease, state.HighestEpochSeen, now).Fence)
        {
            await FenceIfPrimaryAsync(StandbyCauses.LeaseLost, state.HighestEpochSeen, cancellationToken).ConfigureAwait(false);
            return FencingVerdict.NotPrimary;
        }

        return FencingVerdict.Current;
    }

    /// <summary>
    /// Reports that another node claims primary at <paramref name="peerEpoch"/>. A primary fences itself when the claim is newer
    /// (superseded) or equal (split brain). Returns the result so the caller can see <see cref="StandbyTickResult.SplitBrain"/>.
    /// </summary>
    public async Task<StandbyTickResult> ReportPeerClaimAsync(long peerEpoch, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            StandbyState state = _state;
            DateTimeOffset now = _time.GetUtcNow();
            if (state.Role == StandbyRole.Primary && peerEpoch >= state.Epoch)
            {
                string cause = peerEpoch == state.Epoch ? StandbyCauses.SplitBrain : StandbyCauses.Superseded;
                return await FenceAsync(state, cause, Math.Max(state.HighestEpochSeen, peerEpoch), now, cancellationToken).ConfigureAwait(false);
            }

            if (peerEpoch > state.HighestEpochSeen)
            {
                _state = state with { HighestEpochSeen = peerEpoch, At = now };
            }

            return Result(_state, StandbyOutcomes.Idle);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Returns a fenced node to standby after a person approved it (manual failback, doc 46 section 7.6). Refused after a split brain
    /// unless <paramref name="splitBrainResolved"/> is true. Needs the journal: with no record there is no rejoin.
    /// </summary>
    /// <returns>True when the node is a standby again.</returns>
    public async Task<bool> RejoinAsStandbyAsync(bool splitBrainResolved, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            StandbyState state = _state;
            if (state.Role != StandbyRole.Fenced || (state.IsSplitBrain && !splitBrainResolved))
            {
                return false;
            }

            StandbyState next = state with { Role = StandbyRole.Standby, Cause = StandbyCauses.ManualRejoin, At = _time.GetUtcNow() };
            try
            {
                await _journal.AppendAsync(next.At, StandbyJournalKinds.Rejoined, StandbyCodec.Serialize(next), cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                return false;
            }

            _state = next;
            await TrySaveAsync(next, cancellationToken).ConfigureAwait(false);
            return true;
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<StandbyTickResult> TickPrimaryAsync(StandbyState state, CancellationToken cancellationToken)
    {
        DateTimeOffset now = _time.GetUtcNow();
        SelfFenceDecision fence = SelfFence.Evaluate(state.Role, state.Epoch, state.Lease, state.HighestEpochSeen, now);
        if (fence.Fence)
        {
            return await FenceAsync(state, fence.Cause!, state.HighestEpochSeen, now, cancellationToken).ConfigureAwait(false);
        }

        if (state.Lease!.ExpiresAt - now > _options.RenewBefore)
        {
            return Result(state, StandbyOutcomes.Idle);
        }

        LeaseRecord proposed = LeaseRecord.Create(_nodeId, new FencingToken(state.Epoch), now, _options.LeaseDuration);
        (List<string> granted, long highest) = await RequestAllAsync(proposed, state.HighestEpochSeen, cancellationToken).ConfigureAwait(false);
        if (highest > state.Epoch)
        {
            return await FenceAsync(state, StandbyCauses.Superseded, highest, _time.GetUtcNow(), cancellationToken).ConfigureAwait(false);
        }

        if (_policy.EvaluateGrants(granted).Met)
        {
            StandbyState renewed = state with { Lease = proposed, At = now };
            _state = renewed;
            await TrySaveAsync(renewed, cancellationToken).ConfigureAwait(false);
            return Result(renewed, StandbyOutcomes.Renewed);
        }

        DateTimeOffset after = _time.GetUtcNow();
        return SelfFence.Evaluate(state.Role, state.Epoch, state.Lease, highest, after).Fence
            ? await FenceAsync(state, StandbyCauses.LeaseLost, highest, after, cancellationToken).ConfigureAwait(false)
            : Result(state, StandbyOutcomes.RenewFailed);
    }

    private async Task<StandbyTickResult> TickStandbyAsync(StandbyState state, CancellationToken cancellationToken)
    {
        DateTimeOffset now = _time.GetUtcNow();
        IReadOnlyList<WitnessReport> reports = await ObserveAllAsync(state.Lease?.HolderId ?? _peerId, cancellationToken).ConfigureAwait(false);
        LeaseRecord? best = state.Lease;
        long highest = state.HighestEpochSeen;
        foreach (WitnessReport report in reports)
        {
            if (report.HighestLease is { IsWellFormed: true } lease)
            {
                best = LeaseRecord.Newer(best, lease);
                highest = Math.Max(highest, lease.Epoch);
            }
        }

        if (!ReferenceEquals(best, state.Lease) || highest != state.HighestEpochSeen)
        {
            state = state with { Lease = best, HighestEpochSeen = highest, At = now };
            _state = state;
            await TrySaveAsync(state, cancellationToken).ConfigureAwait(false);
        }

        FailoverDecision decision = FailoverDecider.Decide(new FailoverInput(
            state.Role, best, highest, now, _options.ClockSkewMargin, _policy.EvaluateUnreachable(reports)));
        return decision.Action == FailoverAction.Promote
            ? await TryAcquireAsync(state, decision.NewEpoch, now, StandbyCauses.Promoted, cancellationToken).ConfigureAwait(false)
            : Result(state, decision.Reason);
    }

    /// <summary>Asks the witnesses for the lease at <paramref name="epoch"/> and, with a quorum, journals, saves and becomes primary.</summary>
    private async Task<StandbyTickResult> TryAcquireAsync(StandbyState state, FencingToken epoch, DateTimeOffset now, string cause, CancellationToken cancellationToken)
    {
        LeaseRecord proposed = LeaseRecord.Create(_nodeId, epoch, now, _options.LeaseDuration);
        (List<string> granted, long highest) = await RequestAllAsync(proposed, state.HighestEpochSeen, cancellationToken).ConfigureAwait(false);
        StandbyState seen = state with { HighestEpochSeen = Math.Max(highest, state.HighestEpochSeen) };
        if (highest > epoch.Epoch)
        {
            _state = seen;
            return Result(seen, StandbyOutcomes.GrantQuorumMissing);
        }

        if (!_policy.EvaluateGrants(granted).Met)
        {
            _state = seen;
            return Result(seen, StandbyOutcomes.GrantQuorumMissing);
        }

        DateTimeOffset commitAt = _time.GetUtcNow();
        if (proposed.IsExpired(commitAt))
        {
            return Result(seen, StandbyOutcomes.LeaseExpiredBeforeCommit);
        }

        StandbyState next = seen with
        {
            Role = StandbyRole.Primary,
            Epoch = epoch.Epoch,
            HighestEpochSeen = Math.Max(seen.HighestEpochSeen, epoch.Epoch),
            Lease = proposed,
            Cause = cause,
            At = commitAt,
        };
        try
        {
            await _journal.AppendAsync(commitAt, StandbyJournalKinds.Promoted, StandbyCodec.Serialize(next), cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return Result(seen, StandbyOutcomes.JournalUnavailable);
        }

        try
        {
            await _store.SaveAsync(next, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            await TryJournalAsync(seen with { At = commitAt }, StandbyJournalKinds.PromotionAborted, commitAt, cancellationToken).ConfigureAwait(false);
            return Result(seen, StandbyOutcomes.StoreUnavailable);
        }

        _state = next;
        return Result(next, StandbyOutcomes.Promoted);
    }

    /// <summary>Fences the node. The in-memory state changes first; journal and save are best effort and never block it.</summary>
    private async Task<StandbyTickResult> FenceAsync(StandbyState state, string cause, long highestEpoch, DateTimeOffset now, CancellationToken cancellationToken)
    {
        StandbyState next = state with
        {
            Role = StandbyRole.Fenced,
            Cause = cause,
            HighestEpochSeen = Math.Max(state.HighestEpochSeen, highestEpoch),
            At = now.ToUniversalTime(),
        };
        _state = next;
        await TryJournalAsync(next, StandbyJournalKinds.Fenced, next.At, cancellationToken).ConfigureAwait(false);
        await TrySaveAsync(next, cancellationToken).ConfigureAwait(false);
        return new StandbyTickResult(StandbyRole.Fenced, next.Epoch, StandbyOutcomes.Fenced, cause);
    }

    private async Task FenceIfPrimaryAsync(string cause, long highestEpoch, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            StandbyState state = _state;
            if (state.Role == StandbyRole.Primary)
            {
                await FenceAsync(state, cause, highestEpoch, _time.GetUtcNow(), cancellationToken).ConfigureAwait(false);
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<IReadOnlyList<WitnessReport>> ObserveAllAsync(string primaryId, CancellationToken cancellationToken)
    {
        Task<WitnessReport?>[] calls = _witnesses
            .Select(w => CallAsync(async token => await w.ObserveAsync(primaryId, token).ConfigureAwait(false), cancellationToken))
            .ToArray();
        WitnessReport?[] results = await Task.WhenAll(calls).ConfigureAwait(false);
        List<WitnessReport> reports = [];
        for (int i = 0; i < _witnesses.Length; i++)
        {
            // The controller names the witness itself, so a report cannot speak for another witness.
            reports.Add(results[i] is { } report
                ? report with { WitnessId = _witnesses[i].Id }
                : new WitnessReport(_witnesses[i].Id, WitnessAnswer.NoAnswer, null));
        }

        return reports;
    }

    private async Task<(List<string> Granted, long HighestEpoch)> RequestAllAsync(LeaseRecord proposed, long highestKnown, CancellationToken cancellationToken)
    {
        Task<GrantResponse?>[] calls = _witnesses
            .Select(w => CallAsync(async token => await w.RequestGrantAsync(proposed, token).ConfigureAwait(false), cancellationToken))
            .ToArray();
        GrantResponse?[] results = await Task.WhenAll(calls).ConfigureAwait(false);
        List<string> granted = [];
        long highest = highestKnown;
        for (int i = 0; i < _witnesses.Length; i++)
        {
            if (results[i] is not { } response)
            {
                continue;
            }

            highest = Math.Max(highest, response.HighestEpochSeen);
            if (response.Granted)
            {
                granted.Add(_witnesses[i].Id);
            }
        }

        return (granted, highest);
    }

    /// <summary>Runs one witness call with its own timeout. A throw or a timeout gives null. Only the caller's cancellation propagates.</summary>
    private async Task<T?> CallAsync<T>(Func<CancellationToken, Task<T>> call, CancellationToken cancellationToken)
        where T : class
    {
        using CancellationTokenSource timeout = new(_options.WitnessTimeout, _time);
        using CancellationTokenSource linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeout.Token);
        try
        {
            return await call(linked.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            return null;
        }
    }

    private async Task TryJournalAsync(StandbyState state, string kind, DateTimeOffset at, CancellationToken cancellationToken)
    {
        try
        {
            await _journal.AppendAsync(at, kind, StandbyCodec.Serialize(state), cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Best effort: a journal failure never blocks a rejection or a fence.
        }
    }

    private async Task TrySaveAsync(StandbyState state, CancellationToken cancellationToken)
    {
        try
        {
            await _store.SaveAsync(state, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Best effort: the in-memory state carries the truth, and a restart that cannot read it fences the node.
        }
    }

    private static StandbyTickResult Result(StandbyState state, string outcome) => new(state.Role, state.Epoch, outcome);
}
