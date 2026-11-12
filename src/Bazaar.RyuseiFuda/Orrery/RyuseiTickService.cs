using Bazaar.Server.Gc;
using Bazaar.Server.Gc.Create;
using Bazaar.Server.Gc.Write;
using Bazaar.Server.Security.Audit;
using Npgsql;

namespace Bazaar.RyuseiFuda.Orrery;

/// <summary>
/// The RyuseiFuda scheduler tick (doc 38, Layer 2 firing, WP-R5 and WP-R6). One pass claims due timer, interval and armed watcher
/// triggers with <c>FOR UPDATE SKIP LOCKED</c>, decides each with <see cref="RyuseiTickRules"/>, and in one transaction writes the
/// fire through <see cref="RyuseiFireWriter"/>: the <c>ryusei_fire</c> row, the trigger's runtime columns (version-checked) and one
/// inbox item per woken target. The inbox item's source key is the fire id (doc 06, <c>(agent_id, source_key, kind)</c>), so a
/// retried fire is a no-op.
/// </summary>
/// <remarks>
/// The tick enqueues wakes; it never starts a turn, so the normal wake priority and digest apply (doc 38). Triggers have no
/// authority of their own, so a fire writes no task state and no GC line here (WP-R7 adds the attach and new-GC pings).
/// </remarks>
public sealed class RyuseiTickService
{
    /// <summary>The most triggers one pass claims. A later pass takes the rest.</summary>
    public const int BatchSize = 100;

    private readonly NpgsqlDataSource _dataSource;
    private readonly RyuseiTriggerRepository _triggers;
    private readonly RyuseiFireWriter _writer;
    private readonly RyuseiLimits _limits;
    private readonly TimeProvider _time;
    private readonly Func<RyuseiTriggerRecord, TimeZoneInfo> _zoneOf;
    private readonly IGcLogWriter? _gcLog;
    private readonly GcCreateService? _gcCreate;

    /// <param name="zoneOf">
    /// The zone that anchors an interval's wall-clock days. Null means UTC until doc 09 zones are stored (no table holds
    /// them yet).
    /// </param>
    public RyuseiTickService(
        NpgsqlDataSource dataSource,
        RyuseiLimits limits,
        TimeProvider time,
        Func<RyuseiTriggerRecord, TimeZoneInfo>? zoneOf = null,
        IGcLogWriter? gcLog = null,
        GcCreateService? gcCreate = null,
        ITransactionalAuditLog? audit = null)
    {
        ArgumentNullException.ThrowIfNull(dataSource);
        ArgumentNullException.ThrowIfNull(limits);
        ArgumentNullException.ThrowIfNull(time);
        _dataSource = dataSource;
        _limits = limits;
        _time = time;
        _zoneOf = zoneOf ?? (_ => TimeZoneInfo.Utc);
        _gcLog = gcLog;
        _gcCreate = gcCreate;
        _triggers = new RyuseiTriggerRepository(dataSource);
        // A null audit log is allowed (for example, in tests that build the tick directly). The fire rows are still written, and
        // the fire audit line is skipped, so nothing else depends on the audit being installed.
        _writer = new RyuseiFireWriter(dataSource, audit);
    }

    /// <summary>Runs one pass and returns how many fire rows it wrote (woke, refused or skipped).</summary>
    public async Task<int> TickAsync(CancellationToken ct = default)
    {
        var now = _time.GetUtcNow();
        await using var conn = await _dataSource.OpenConnectionAsync(ct).ConfigureAwait(false);
        await using var tx = await conn.BeginTransactionAsync(ct).ConfigureAwait(false);

        var due = await _triggers.ClaimDueAsync(tx, now, BatchSize, ct).ConfigureAwait(false);
        var written = 0;
        foreach (var trigger in due)
        {
            if (await ProcessAsync(tx, trigger, now, ct).ConfigureAwait(false))
            {
                written++;
            }
        }

        await tx.CommitAsync(ct).ConfigureAwait(false);
        return written;
    }

    private async Task<bool> ProcessAsync(NpgsqlTransaction tx, RyuseiTriggerRecord trigger, DateTimeOffset now, CancellationToken ct)
    {
        if (RyuseiTickRules.IsExpiredWatcher(trigger, now))
        {
            // Expired before its armed fire: nothing fired, so there is no fire row.
            await _writer.ExpireAsync(tx, trigger, ct).ConfigureAwait(false);
            return false;
        }

        RyuseiFireDecision? decision;
        var spec = RyuseiFireWriter.TryDecodeSpec(trigger);
        if (spec is null)
        {
            decision = RyuseiTickRules.Malformed(trigger, "The stored spec does not decode.");
        }
        else
        {
            var input = await BuildInputAsync(tx, trigger, spec, now, ct).ConfigureAwait(false);
            decision = RyuseiTickRules.Decide(input, now, _limits);
        }

        if (decision is null)
        {
            return false;
        }

        // An attach fire posts its system line first (WP-R7). The line is idempotent on (trigger, fire instant), so a retried
        // batch returns the same seq and the inbox keys stay stable.
        RyuseiPingPosted? ping = null;
        if (decision.Woke && trigger.PingMode != RyuseiPingMode.None)
        {
            (ping, decision) = await PostPingAsync(trigger, decision, ct).ConfigureAwait(false);
        }

        return await _writer.ApplyAsync(tx, trigger, decision, now, ping, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Posts the ping line for an attach fire, or creates the GC and then posts its line for a new_gc fire. Returns the posted
    /// line, or null and an error decision when the GC cannot take it. An error decision wakes nobody, so the fire is recorded
    /// instead of poisoning the batch. A new GC is created in its own transaction first, so the line's GC exists when the line
    /// goes in. Both steps are idempotent on (trigger, fire instant).
    /// </summary>
    private async Task<(RyuseiPingPosted? Ping, RyuseiFireDecision Decision)> PostPingAsync(
        RyuseiTriggerRecord trigger,
        RyuseiFireDecision decision,
        CancellationToken ct)
    {
        if (_gcLog is null)
        {
            return (null, RyuseiTickRules.Malformed(trigger, "No GC log is installed, so the ping cannot be posted."));
        }

        Guid gcId;
        if (trigger.PingMode == RyuseiPingMode.Attach)
        {
            if (trigger.AttachGcId is not { } attachGc)
            {
                return (null, RyuseiTickRules.Malformed(trigger, "The attach ping has no attach GC."));
            }

            gcId = attachGc;
        }
        else
        {
            if (_gcCreate is null)
            {
                return (null, RyuseiTickRules.Malformed(trigger, "No GC creation service is installed, so the new GC cannot be made."));
            }

            gcId = GcCreateService.DeterministicGcId($"ryusei-gc:{trigger.TriggerId:D}:{decision.ScheduledFor.ToUniversalTime():O}");
            var members = trigger.TargetAgentIds.Append(trigger.CreatedBy).Distinct().ToArray();
            try
            {
                await _gcCreate.EnsureAsync(
                    new GcCreateRequest(gcId, $"RyuseiFuda: {trigger.Purpose}", trigger.CreatedBy, members),
                    ct).ConfigureAwait(false);
            }
            catch (GcCreateRefusedException)
            {
                return (null, RyuseiTickRules.Malformed(trigger, "The new GC was refused by its create rules."));
            }
        }

        try
        {
            var request = RyuseiPingLine.Build(trigger, gcId, decision.ScheduledFor, decision.WakeAgentIds);
            var result = await _gcLog.AppendAsync(request, ct).ConfigureAwait(false);
            return (new RyuseiPingPosted(result.GcId, result.Seq), decision);
        }
        catch (Exception ex) when (ex is GcNotFoundException or GcArchivedException)
        {
            return (null, RyuseiTickRules.Malformed(trigger, "The ping GC is missing or archived."));
        }
    }

    private async Task<RyuseiTickInput> BuildInputAsync(
        NpgsqlTransaction tx,
        RyuseiTriggerRecord trigger,
        RyuseiTriggerSpec spec,
        DateTimeOffset now,
        CancellationToken ct)
    {
        var context = await RyuseiFireRepository.LoadContextAsync(tx, trigger, now, ct).ConfigureAwait(false);
        return new RyuseiTickInput(
            trigger,
            spec,
            _zoneOf(trigger),
            context.Targets,
            context.FiresInLast24Hours,
            context.LastWokeAt,
            context.WakesInLastHour);
    }
}
