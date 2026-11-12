using System.Text.Json.Nodes;
using Bazaar.Server.Kanban.Data;
using Bazaar.Server.Security.Audit;
using Npgsql;

namespace Bazaar.RyuseiFuda.Orrery;

/// <summary>Outcome class of a trigger service call.</summary>
public enum RyuseiTriggerStatus
{
    Ok,

    /// <summary>A rule or authority check refused the action. <see cref="RyuseiTriggerResult.Refusal"/> says which.</summary>
    Refused,

    NotFound,

    /// <summary>The trigger is in a state the action does not apply to, for example cancelling a fired trigger.</summary>
    InvalidState,

    /// <summary>Another writer changed the trigger first. Nothing was written.</summary>
    Conflict,
}

/// <summary>Result of a trigger service call. <see cref="TriggerId"/> and <see cref="Version"/> are set when the call succeeded.</summary>
public sealed record RyuseiTriggerResult(
    RyuseiTriggerStatus Status,
    Guid? TriggerId,
    long? Version,
    RyuseiRefusal Refusal,
    string Message)
{
    public bool IsOk => Status == RyuseiTriggerStatus.Ok;

    public static RyuseiTriggerResult Ok(Guid triggerId, long version) =>
        new(RyuseiTriggerStatus.Ok, triggerId, version, RyuseiRefusal.None, string.Empty);

    public static RyuseiTriggerResult Refused(RyuseiRefusal refusal, string message) =>
        new(RyuseiTriggerStatus.Refused, null, null, refusal, message);

    public static RyuseiTriggerResult Other(RyuseiTriggerStatus status, string message) =>
        new(status, null, null, RyuseiRefusal.None, message);
}

/// <summary>
/// The only writer of <c>ryusei_trigger</c> state (doc 38, Data model and Authority). Each change runs in one transaction
/// and writes one audit row through <see cref="IAuditLog"/> after it commits. Refusals are audited too.
/// </summary>
/// <remarks>
/// Creation takes a transaction-scoped advisory lock keyed on the project, so two creations in one project cannot both pass
/// the active-trigger cap. Checks that need no database (shape, ping, chain depth, spec, authority) run before the lock, so a
/// refused request never opens a transaction.
/// </remarks>
public sealed class RyuseiTriggerService
{
    private readonly NpgsqlDataSource _dataSource;
    private readonly RyuseiTriggerRepository _triggers;
    private readonly ITransactionalAuditLog _audit;
    private readonly RyuseiLimits _limits;
    private readonly IRyuseiObservatory _observatory;

    /// <param name="observatory">The Observatory sink (doc 23). Null emits nothing. Events go out only after a commit.</param>
    public RyuseiTriggerService(
        NpgsqlDataSource dataSource,
        ITransactionalAuditLog audit,
        RyuseiLimits? limits = null,
        IRyuseiObservatory? observatory = null)
    {
        ArgumentNullException.ThrowIfNull(dataSource);
        ArgumentNullException.ThrowIfNull(audit);
        _dataSource = dataSource;
        _audit = audit;
        _limits = limits ?? new RyuseiLimits();
        _triggers = new RyuseiTriggerRepository(dataSource);
        _observatory = observatory ?? NullRyuseiObservatory.Instance;
    }

    /// <summary>
    /// Creates a trigger. <paramref name="targets"/> carries each target's project and active flag as the caller resolved
    /// them. <paramref name="zone"/> is the first target's wall-clock zone for an interval (null means UTC).
    /// <paramref name="coworker"/> is the <c>ryusei.coworker_triggers</c> value.
    /// </summary>
    public async Task<RyuseiTriggerResult> CreateAsync(
        RyuseiActor creator,
        Guid projectId,
        IReadOnlyList<RyuseiTargetInfo> targets,
        RyuseiTriggerKind kind,
        RyuseiTriggerSpec spec,
        string purpose,
        Guid? taskId,
        Guid? attachGcId,
        RyuseiPingMode pingMode,
        int chainDepth,
        DateTimeOffset now,
        TimeZoneInfo? zone = null,
        RyuseiCoworkerTriggers coworker = RyuseiCoworkerTriggers.Allowed,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(targets);
        ArgumentNullException.ThrowIfNull(spec);

        var request = new RyuseiCreateRequest(creator, projectId, targets, kind, spec, purpose, attachGcId, pingMode, chainDepth, coworker);
        var check = RyuseiTriggerRules.CheckCreate(request, _limits);
        if (!check.IsAllowed)
        {
            return await RefuseCreateAsync(creator, projectId, kind, check, now, ct).ConfigureAwait(false);
        }

        var agentIds = targets.Select(t => t.AgentId).ToArray();
        await using var conn = await _dataSource.OpenConnectionAsync(ct).ConfigureAwait(false);
        await using var tx = await conn.BeginTransactionAsync(ct).ConfigureAwait(false);

        await using (var lockCmd = new NpgsqlCommand("SELECT pg_advisory_xact_lock(hashtextextended(@key, 0))", conn, tx))
        {
            lockCmd.Parameters.AddWithValue("key", $"ryusei-trigger:{projectId:D}");
            await lockCmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }

        var live = await _triggers.CountLiveByTargetAsync(tx, agentIds, ct).ConfigureAwait(false);
        var caps = RyuseiTriggerRules.CheckActiveCaps(agentIds, live, _limits.MaxTriggersPerAgent);
        if (!caps.IsAllowed)
        {
            await tx.RollbackAsync(ct).ConfigureAwait(false);
            return await RefuseCreateAsync(creator, projectId, kind, caps, now, ct).ConfigureAwait(false);
        }

        var (nextFireAt, maxFires, expiresAt) = Schedule(spec, now, zone);
        var triggerId = Guid.CreateVersion7();
        var stored = await _triggers.InsertAsync(tx, new NewRyuseiTrigger(
            triggerId,
            projectId,
            creator.Id,
            kind,
            agentIds,
            purpose,
            RyuseiSpecJson.Encode(spec),
            taskId,
            attachGcId,
            pingMode,
            nextFireAt,
            maxFires,
            expiresAt,
            chainDepth), ct).ConfigureAwait(false);

        // The audit payload and the Observatory event share one set of fields: ids, kind and a purpose hash, never the text.
        var created = RyuseiObservatoryEvents.CreatedFields(triggerId, projectId, kind, agentIds, pingMode, chainDepth, purpose);
        await AuditInTransactionAsync(conn, tx, creator, "ryusei.trigger.created", now, created, ct).ConfigureAwait(false);
        await tx.CommitAsync(ct).ConfigureAwait(false);

        await RyuseiObservatoryEvents.EmitAsync(_observatory, [RyuseiObservatoryEvents.Created(created, now)], ct).ConfigureAwait(false);
        return RyuseiTriggerResult.Ok(triggerId, stored.Version);
    }

    /// <summary>Pauses a trigger. The owner or a target agent may pause it, and a target is never refused.</summary>
    public Task<RyuseiTriggerResult> PauseAsync(RyuseiActor actor, Guid triggerId, DateTimeOffset now, CancellationToken ct = default) =>
        ChangeStateAsync(actor, triggerId, RyuseiChange.Pause, now, ct);

    /// <summary>Resumes a paused trigger. The owner or a target agent may resume it.</summary>
    public Task<RyuseiTriggerResult> ResumeAsync(RyuseiActor actor, Guid triggerId, DateTimeOffset now, CancellationToken ct = default) =>
        ChangeStateAsync(actor, triggerId, RyuseiChange.Resume, now, ct);

    /// <summary>Cancels a trigger. The creator or the owner may cancel it.</summary>
    public Task<RyuseiTriggerResult> CancelAsync(RyuseiActor actor, Guid triggerId, DateTimeOffset now, CancellationToken ct = default) =>
        ChangeStateAsync(actor, triggerId, RyuseiChange.Cancel, now, ct);

    /// <summary>
    /// Lists the triggers that name <paramref name="agentId"/> as a target, oldest first, optionally in one state, at most
    /// <paramref name="limit"/> rows. This is a read: it writes no audit row and applies no authority rule, because a target
    /// may always see the triggers that name it (doc 38, coworker triggers). The filter runs after the target query, so a
    /// very busy agent reads more rows than the page keeps.
    /// </summary>
    public async Task<IReadOnlyList<RyuseiTriggerRecord>> ListForTargetAsync(
        Guid agentId,
        RyuseiTriggerState? state,
        int limit,
        CancellationToken ct = default)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(limit, 1);

        var all = await _triggers.ListByTargetAgentAsync(agentId, ct).ConfigureAwait(false);
        return all.Where(t => state is null || t.State == state.Value).Take(limit).ToList();
    }

    private enum RyuseiChange
    {
        Pause,
        Resume,
        Cancel,
    }

    private async Task<RyuseiTriggerResult> ChangeStateAsync(
        RyuseiActor actor,
        Guid triggerId,
        RyuseiChange change,
        DateTimeOffset now,
        CancellationToken ct)
    {
        await using var conn = await _dataSource.OpenConnectionAsync(ct).ConfigureAwait(false);
        await using var tx = await conn.BeginTransactionAsync(ct).ConfigureAwait(false);

        var record = await _triggers.GetForUpdateAsync(tx, triggerId, ct).ConfigureAwait(false);
        if (record is null)
        {
            return RyuseiTriggerResult.Other(RyuseiTriggerStatus.NotFound, $"No trigger {triggerId:D}.");
        }

        var check = change == RyuseiChange.Cancel
            ? RyuseiAuthority.CheckCancel(actor, record.CreatedBy)
            : RyuseiAuthority.CheckPauseOrResume(actor, record.TargetAgentIds);
        if (!check.IsAllowed)
        {
            await tx.RollbackAsync(ct).ConfigureAwait(false);
            await AuditAsync(actor, "ryusei.trigger.refused", now, RefusedPayload(record.ProjectId, change.ToString(), check), ct).ConfigureAwait(false);
            return RyuseiTriggerResult.Refused(check.Refusal, check.Message);
        }

        var target = NextState(record.State, change);
        if (target is null)
        {
            return RyuseiTriggerResult.Other(
                RyuseiTriggerStatus.InvalidState,
                $"The trigger is {RyuseiText.ToSql(record.State)}, so it cannot be {change.ToString().ToLowerInvariant()}d.");
        }

        if (target == record.State)
        {
            // Already in the requested state. Nothing changes and nothing is audited.
            return RyuseiTriggerResult.Ok(triggerId, record.Version);
        }

        var updated = await _triggers.UpdateAsync(tx, record with { State = target.Value }, record.Version, ct).ConfigureAwait(false);
        if (!updated)
        {
            return RyuseiTriggerResult.Other(RyuseiTriggerStatus.Conflict, "The trigger changed while this request was running. Nothing was written.");
        }

        var fields = RyuseiObservatoryEvents.StateChangedFields(triggerId, record.ProjectId, record.State, target.Value);
        await AuditInTransactionAsync(conn, tx, actor, $"ryusei.trigger.{ChangeVerb(change)}", now, fields, ct).ConfigureAwait(false);
        await tx.CommitAsync(ct).ConfigureAwait(false);

        var evt = new RyuseiObservatoryEvent(RyuseiObservatoryEvents.NameForStateChange(target.Value), now, fields);
        await RyuseiObservatoryEvents.EmitAsync(_observatory, [evt], ct).ConfigureAwait(false);
        return RyuseiTriggerResult.Ok(triggerId, record.Version + 1);
    }

    /// <summary>The state after a change, or null when the change does not apply. A change to the current state is a no-op.</summary>
    private static RyuseiTriggerState? NextState(RyuseiTriggerState state, RyuseiChange change) => (change, state) switch
    {
        (RyuseiChange.Pause, RyuseiTriggerState.Active) => RyuseiTriggerState.Paused,
        (RyuseiChange.Pause, RyuseiTriggerState.Paused) => RyuseiTriggerState.Paused,
        (RyuseiChange.Resume, RyuseiTriggerState.Paused) => RyuseiTriggerState.Active,
        (RyuseiChange.Resume, RyuseiTriggerState.Active) => RyuseiTriggerState.Active,
        (RyuseiChange.Cancel, RyuseiTriggerState.Active or RyuseiTriggerState.Paused) => RyuseiTriggerState.Cancelled,
        (RyuseiChange.Cancel, RyuseiTriggerState.Cancelled) => RyuseiTriggerState.Cancelled,
        _ => null,
    };

    private static string ChangeVerb(RyuseiChange change) => change switch
    {
        RyuseiChange.Pause => "paused",
        RyuseiChange.Resume => "resumed",
        _ => "cancelled",
    };

    /// <summary>
    /// The first <c>next_fire_at</c>, <c>max_fires</c> and <c>expires_at</c> for a new row. A timer keeps its instant, which
    /// may already be past (it is then due at once). An interval is due now when its first occurrence has passed, and
    /// otherwise at its anchor. A watcher is idle at creation and stores null: an event match arms it with a debounce deadline
    /// (WP-R6). An event has no time-based fire, so it stores null.
    /// </summary>
    private (DateTimeOffset? NextFireAt, long? MaxFires, DateTimeOffset? ExpiresAt) Schedule(RyuseiTriggerSpec spec, DateTimeOffset now, TimeZoneInfo? zone) =>
        spec switch
        {
            RyuseiTimerTriggerSpec timer => (timer.FireAt, null, null),
            RyuseiIntervalTriggerSpec interval => (IntervalFirst(interval, now, zone), null, null),
            RyuseiWatcherTriggerSpec watcher => ((DateTimeOffset?)null, watcher.MaxFires, watcher.ExpiresAt),
            _ => (null, null, null),
        };

    private DateTimeOffset IntervalFirst(RyuseiIntervalTriggerSpec interval, DateTimeOffset now, TimeZoneInfo? zone)
    {
        var schedule = RyuseiSchedule.NextFire(interval, now, zone ?? TimeZoneInfo.Utc, _limits.MinInterval);
        return schedule.IsDue ? now : schedule.NextRunAt;
    }

    private async Task<RyuseiTriggerResult> RefuseCreateAsync(
        RyuseiActor creator,
        Guid projectId,
        RyuseiTriggerKind kind,
        RyuseiCheck check,
        DateTimeOffset now,
        CancellationToken ct)
    {
        var payload = RefusedPayload(projectId, RyuseiText.ToSql(kind), check);
        await AuditAsync(creator, "ryusei.trigger.refused", now, payload, ct).ConfigureAwait(false);
        return RyuseiTriggerResult.Refused(check.Refusal, check.Message);
    }

    private static JsonObject RefusedPayload(Guid projectId, string what, RyuseiCheck check) => new()
    {
        ["projectId"] = projectId.ToString("D"),
        ["what"] = what,
        ["refusal"] = check.Refusal.ToString(),
        ["message"] = check.Message,
    };

    /// <summary>Audits a refusal. A refusal changes no state, so it is appended on its own, outside any transaction.</summary>
    private Task AuditAsync(RyuseiActor actor, string kind, DateTimeOffset now, JsonObject payload, CancellationToken ct) =>
        _audit.AppendAsync(new AuditEntry(now, actor.ToAuditText(), kind, payload.ToJsonString()), ct);

    /// <summary>Audits a state change inside its transaction, so the change and its audit row commit or roll back together.</summary>
    private Task AuditInTransactionAsync(NpgsqlConnection conn, NpgsqlTransaction tx, RyuseiActor actor, string kind, DateTimeOffset now, JsonObject payload, CancellationToken ct) =>
        _audit.AppendInTransactionAsync(conn, tx, new AuditEntry(now, actor.ToAuditText(), kind, payload.ToJsonString()), ct);
}
