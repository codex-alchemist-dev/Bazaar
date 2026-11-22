using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Bazaar.Kodo.Responder;

namespace Bazaar.Kodo.Incidents;

/// <summary>What opens an incident.</summary>
/// <param name="Trigger">The trigger. Manual needs the enter control and an approval; the automatic four need neither (they only tighten).</param>
/// <param name="Cause">The probe id (with an optional scope) or rule that opened it, as an identifier.</param>
/// <param name="Title">A short plain title, printable ASCII, up to 120 characters.</param>
/// <param name="Severity">The severity. Automatic entry is always critical.</param>
/// <param name="Opener">Who opened it, as an identifier. Ignored for automatic entry, which records <c>kodo</c>.</param>
/// <param name="Reason">Why, as an identifier code.</param>
/// <param name="Categories">The affected categories (component ids).</param>
/// <param name="LinkedAlerts">Fingerprints of the alerts that led here.</param>
/// <param name="GrantedControls">The controls the opener holds. Checked for manual entry only.</param>
public sealed record IncidentOpenRequest(
    IncidentTrigger Trigger,
    string Cause,
    string Title,
    AlertSeverity Severity,
    string Opener,
    string Reason,
    IReadOnlyList<string> Categories,
    IReadOnlyList<string> LinkedAlerts,
    IReadOnlySet<string> GrantedControls);

/// <summary>A request to move an incident to another phase.</summary>
/// <param name="IncidentId">The incident.</param>
/// <param name="To">The phase to enter.</param>
/// <param name="Actor">Who, as an identifier.</param>
/// <param name="Reason">Why, as an identifier code.</param>
/// <param name="GrantedControls">The controls the actor holds. Closing needs <see cref="KodoControls.IncidentExit"/>.</param>
/// <param name="ProbesGreenSince">For resolving: when every probe that opened the incident became green and stayed green.</param>
public sealed record IncidentAdvanceRequest(
    string IncidentId,
    IncidentPhase To,
    string Actor,
    string Reason,
    IReadOnlySet<string> GrantedControls,
    DateTimeOffset? ProbesGreenSince);

/// <summary>What a controller call did.</summary>
/// <param name="Ok">True when the call did what was asked.</param>
/// <param name="Code">A fixed code. <c>ok</c>, or the reason for the refusal or the pending state, for example <c>approval_pending</c>.</param>
/// <param name="Incident">The incident after the call, or the one in the way, or null.</param>
/// <param name="ApprovalId">The approval gate's id when one was involved.</param>
/// <param name="Notification">What the router did with the notification, when one was sent.</param>
public sealed record IncidentResult(bool Ok, string Code, IncidentRecord? Incident, string? ApprovalId = null, AlertRouteResult? Notification = null);

/// <summary>
/// The incident lifecycle (doc 46 section 16.3): opens an incident, applies the freeze, seals the evidence, notifies, moves the phase, and
/// closes it only on a confirmed exit. It does no I/O: the store, journal, freeze sink, evidence sink, approval gate and alert channels are
/// interfaces and time comes from the <see cref="TimeProvider"/>. Every change is journalled first and saved second; if either fails the
/// change is refused and nothing moves. Not thread-safe: one caller at a time.
/// </summary>
public sealed class IncidentController
{
    /// <summary>The actor recorded for changes the system makes itself.</summary>
    public const string SystemActor = "kodo";

    private const string _gateRunbook = "kodo.incident";

    private readonly IIncidentStore _store;
    private readonly IIncidentJournal _journal;
    private readonly IIncidentFreezeSink _freeze;
    private readonly IApprovalGate _gate;
    private readonly IncidentOptions _options;
    private readonly TimeProvider _time;
    private readonly IncidentRecoveryPlanner? _planner;
    private readonly IncidentEvidenceCollector? _evidence;
    private readonly IncidentNotifier? _notifier;

    /// <summary>Creates the controller. The options are validated. The planner, evidence collector and notifier are optional.</summary>
    public IncidentController(
        IIncidentStore store,
        IIncidentJournal journal,
        IIncidentFreezeSink freeze,
        IApprovalGate gate,
        IncidentOptions options,
        TimeProvider timeProvider,
        IncidentRecoveryPlanner? planner = null,
        IncidentEvidenceCollector? evidence = null,
        IncidentNotifier? notifier = null)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(journal);
        ArgumentNullException.ThrowIfNull(freeze);
        ArgumentNullException.ThrowIfNull(gate);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(timeProvider);
        options.Validate();
        _store = store;
        _journal = journal;
        _freeze = freeze;
        _gate = gate;
        _options = options;
        _time = timeProvider;
        _planner = planner;
        _evidence = evidence;
        _notifier = notifier;
    }

    /// <summary>Loads an incident, or null when none exists or the store cannot read it.</summary>
    public async ValueTask<IncidentRecord?> GetAsync(string incidentId, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrEmpty(incidentId);
        try
        {
            return await _store.LoadAsync(incidentId, cancellationToken).ConfigureAwait(false);
        }
        catch (IOException)
        {
            return null;
        }
    }

    /// <summary>The incident that holds the freeze now, or null. Feed it to <see cref="IncidentFreeze"/>.</summary>
    public async ValueTask<IncidentRecord?> ActiveAsync(CancellationToken cancellationToken)
    {
        try
        {
            return (await _store.ListUnsettledAsync(cancellationToken).ConfigureAwait(false)).FirstOrDefault(record => record.IsActive);
        }
        catch (IOException)
        {
            return null;
        }
    }

    /// <summary>
    /// Opens an incident (section 16.3). Manual entry needs the enter control and an approval from the gate. The four automatic triggers
    /// need neither: entering only tightens. Only one incident may be active.
    /// </summary>
    public async ValueTask<IncidentResult> OpenAsync(IncidentOpenRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        string? problem = Problem(request);
        if (problem is not null)
        {
            return Refused(problem);
        }

        IReadOnlyList<IncidentRecord> unsettled;
        try
        {
            unsettled = await _store.ListUnsettledAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (IOException)
        {
            return Refused("state_not_readable");
        }

        if (unsettled.FirstOrDefault(record => record.IsActive) is { } existing)
        {
            return new IncidentResult(false, "incident_already_active", existing);
        }

        bool manual = request.Trigger == IncidentTrigger.Manual;
        string opener = manual ? request.Opener : SystemActor;
        string? approvalId = null;
        if (manual)
        {
            if (!request.GrantedControls.Contains(KodoControls.IncidentEnter))
            {
                return Refused("missing_control");
            }

            string digest = Digest("incident-enter", request.Cause, request.Title, request.Opener);
            (string? code, approvalId) = await RequireApprovalAsync(
                $"enter-{digest[..16]}", "enter", "incident.enter", digest, KodoControls.IncidentEnter, _options.EnterRequiresStepUp, cancellationToken)
                .ConfigureAwait(false);
            if (code is not null)
            {
                return new IncidentResult(false, code, null, approvalId);
            }
        }

        long number;
        try
        {
            number = await _store.NextNumberAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (IOException)
        {
            return Refused("state_not_saved");
        }

        DateTimeOffset now = _time.GetUtcNow();
        IncidentRecord record = new(
            IncidentRecord.CurrentVersion,
            number,
            IncidentRecord.IdFor(number),
            manual ? request.Severity : AlertSeverity.Critical,
            request.Trigger,
            request.Cause,
            request.Title,
            opener,
            request.Reason,
            now,
            IncidentPhase.Opened,
            [.. request.Categories.Distinct(StringComparer.Ordinal)],
            [.. request.LinkedAlerts.Distinct(StringComparer.Ordinal)],
            _planner?.Plan(request.Trigger, request.Cause) ?? [],
            [],
            [],
            false,
            false,
            null,
            0,
            now,
            0);

        string? failure = await CommitAsync(record, IncidentJournalKinds.Opened, opener, request.Reason, cancellationToken).ConfigureAwait(false);
        if (failure is not null)
        {
            return Refused(failure);
        }

        IncidentResult done = await CompleteOpenAsync(record, cancellationToken).ConfigureAwait(false);
        return done with { ApprovalId = approvalId };
    }

    /// <summary>
    /// Continues after a restart: finishes any incident left in <see cref="IncidentPhase.Opened"/>, re-applies the freeze of every open
    /// incident (the sink is idempotent), and lifts the freeze of a closed one whose release was not recorded.
    /// </summary>
    public async ValueTask<IReadOnlyList<IncidentResult>> RecoverAsync(CancellationToken cancellationToken)
    {
        IReadOnlyList<IncidentRecord> unsettled;
        try
        {
            unsettled = await _store.ListUnsettledAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (IOException)
        {
            return [Refused("state_not_readable")];
        }

        List<IncidentResult> results = [];
        foreach (IncidentRecord record in unsettled)
        {
            if (record.Phase == IncidentPhase.Opened)
            {
                results.Add(await CompleteOpenAsync(record, cancellationToken).ConfigureAwait(false));
            }
            else if (!record.IsActive)
            {
                results.Add(await ReleaseAsync(record, cancellationToken).ConfigureAwait(false));
            }
            else
            {
                try
                {
                    await _freeze.ApplyAsync(record.Id, cancellationToken).ConfigureAwait(false);
                    results.Add(new IncidentResult(true, "ok", record));
                }
                catch (IOException)
                {
                    results.Add(new IncidentResult(false, "freeze_pending", record));
                }
            }
        }

        return results;
    }

    /// <summary>
    /// Moves an incident to another phase. Resolving needs the probes green for the stability window. Closing needs the exit control and
    /// an approved, stepped-up answer from the gate; it never closes on a timeout, a pending answer, an error or a mismatch.
    /// </summary>
    public async ValueTask<IncidentResult> AdvanceAsync(IncidentAdvanceRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!IncidentText.IsIdentifier(request.Actor) || !IncidentText.IsIdentifier(request.Reason) || request.GrantedControls is null)
        {
            return Refused("invalid_request");
        }

        IncidentRecord? record = await GetAsync(request.IncidentId, cancellationToken).ConfigureAwait(false);
        if (record is null)
        {
            return Refused("unknown_incident");
        }

        if (request.To == IncidentPhase.Frozen && record.Phase == IncidentPhase.Opened)
        {
            return await CompleteOpenAsync(record, cancellationToken).ConfigureAwait(false);
        }

        if (!IncidentStateMachine.IsValid(record.Phase, request.To))
        {
            return new IncidentResult(false, "invalid_transition", record);
        }

        DateTimeOffset now = _time.GetUtcNow();
        bool closing = request.To is IncidentPhase.Resolved or IncidentPhase.Closed;
        if (record.Transitions.Count >= (closing ? IncidentRecord.MaxTransitions : IncidentRecord.MaxTransitions - 2))
        {
            return new IncidentResult(false, "timeline_full", record);
        }

        string? approvalId = null;
        if (request.To == IncidentPhase.Resolved
            && (request.ProbesGreenSince is not { } since || since > now || now - since < _options.StabilityWindow))
        {
            return new IncidentResult(false, "probes_not_stable", record);
        }

        if (request.To == IncidentPhase.Closed)
        {
            if (!request.GrantedControls.Contains(KodoControls.IncidentExit))
            {
                return new IncidentResult(false, "missing_control", record);
            }

            string digest = Digest("incident-exit", record.Id, record.Number.ToString(CultureInfo.InvariantCulture));
            (string? code, approvalId) = await RequireApprovalAsync(
                record.Id, "exit", "incident.exit", digest, KodoControls.IncidentExit, true, cancellationToken).ConfigureAwait(false);
            if (code is not null)
            {
                if (code is "approval_denied")
                {
                    await TryJournalAsync(record, IncidentJournalKinds.ApprovalDenied, request.Actor, "exit", cancellationToken).ConfigureAwait(false);
                }

                return new IncidentResult(false, code, record, approvalId);
            }
        }

        IncidentRecord next = Step(record, request.To, now, request.Actor, request.Reason, approvalId);
        string? failure = await CommitAsync(next, IncidentJournalKinds.Transition, request.Actor, $"{record.Phase}-{request.To}".ToLowerInvariant(), cancellationToken)
            .ConfigureAwait(false);
        if (failure is not null)
        {
            return new IncidentResult(false, failure, record);
        }

        AlertRouteResult? notice = null;
        if (request.To == IncidentPhase.Resolved)
        {
            notice = await NotifyAsync(next, IncidentNotice.Resolved, cancellationToken).ConfigureAwait(false);
        }

        if (request.To != IncidentPhase.Closed)
        {
            return new IncidentResult(true, "ok", next, approvalId, notice);
        }

        IncidentResult released = await ReleaseAsync(next, cancellationToken).ConfigureAwait(false);
        notice = await NotifyAsync(released.Incident ?? next, IncidentNotice.Closed, cancellationToken).ConfigureAwait(false);
        return released with { ApprovalId = approvalId, Notification = notice };
    }

    /// <summary>Links a runbook run to the incident and, from investigating, moves it to recovering. Journalled first, saved second.</summary>
    public async ValueTask<IncidentResult> LinkRunAsync(string incidentId, IncidentRunLink link, string actor, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(link);
        if (!IncidentText.IsIdentifier(actor) || !IncidentText.IsIdentifier(link.RunId) || !IncidentText.IsComponent(link.RunbookId))
        {
            return Refused("invalid_request");
        }

        IncidentRecord? record = await GetAsync(incidentId, cancellationToken).ConfigureAwait(false);
        if (record is null)
        {
            return Refused("unknown_incident");
        }

        if (!record.IsActive || record.Runs.Count >= IncidentRecord.MaxRuns || record.Runs.Any(run => run.RunId == link.RunId))
        {
            return new IncidentResult(false, "cannot_link", record);
        }

        DateTimeOffset now = _time.GetUtcNow();
        IncidentRecord next = record with { Runs = [.. record.Runs, link], UpdatedAt = now, Sequence = record.Sequence + 1 };
        if (record.Phase == IncidentPhase.Investigating && record.Transitions.Count < IncidentRecord.MaxTransitions - 2)
        {
            next = Step(next, IncidentPhase.Recovering, now, actor, "run_started", null);
        }

        string? failure = await CommitAsync(next, IncidentJournalKinds.RunLinked, actor, link.RunbookId, cancellationToken).ConfigureAwait(false);
        return failure is null ? new IncidentResult(true, "ok", next) : new IncidentResult(false, failure, record);
    }

    /// <summary>Records the phase a linked run was last seen in.</summary>
    public async ValueTask<IncidentResult> UpdateRunAsync(string incidentId, IncidentRunLink link, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(link);
        IncidentRecord? record = await GetAsync(incidentId, cancellationToken).ConfigureAwait(false);
        if (record is null)
        {
            return Refused("unknown_incident");
        }

        int index = record.Runs.ToList().FindIndex(run => run.RunId == link.RunId);
        if (index < 0)
        {
            return new IncidentResult(false, "unknown_run", record);
        }

        List<IncidentRunLink> runs = [.. record.Runs];
        runs[index] = link;
        IncidentRecord next = record with { Runs = runs, UpdatedAt = _time.GetUtcNow(), Sequence = record.Sequence + 1 };
        string? failure = await CommitAsync(next, IncidentJournalKinds.RunUpdated, SystemActor, link.Phase.ToString().ToLowerInvariant(), cancellationToken)
            .ConfigureAwait(false);
        return failure is null ? new IncidentResult(true, "ok", next) : new IncidentResult(false, failure, record);
    }

    private async ValueTask<IncidentResult> CompleteOpenAsync(IncidentRecord record, CancellationToken cancellationToken)
    {
        try
        {
            await _freeze.ApplyAsync(record.Id, cancellationToken).ConfigureAwait(false);
        }
        catch (IOException)
        {
            return new IncidentResult(false, "freeze_pending", record);
        }

        string? root = record.EvidenceRoot;
        int itemCount = record.EvidenceItems;
        if (_evidence is not null && root is null)
        {
            try
            {
                EvidenceManifest manifest = await _evidence.CollectAsync(record.Id, record.OpenedAt, cancellationToken).ConfigureAwait(false);
                root = manifest.Root;
                itemCount = manifest.Items.Count;
                await TryJournalAsync(record, IncidentJournalKinds.EvidenceSealed, SystemActor, root[..16], cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception)
            {
                await TryJournalAsync(record, IncidentJournalKinds.EvidenceFailed, SystemActor, "snapshot_failed", cancellationToken).ConfigureAwait(false);
            }
        }

        DateTimeOffset now = _time.GetUtcNow();
        IncidentRecord next = Step(record, IncidentPhase.Frozen, now, SystemActor, "freeze_applied", null) with
        {
            FreezeApplied = true,
            EvidenceRoot = root,
            EvidenceItems = itemCount,
        };
        string? failure = await CommitAsync(next, IncidentJournalKinds.Transition, SystemActor, "opened-frozen", cancellationToken).ConfigureAwait(false);
        if (failure is not null)
        {
            return new IncidentResult(false, failure, record);
        }

        AlertRouteResult? notice = await NotifyAsync(next, IncidentNotice.Opened, cancellationToken).ConfigureAwait(false);
        return new IncidentResult(true, "ok", next, null, notice);
    }

    private async ValueTask<IncidentResult> ReleaseAsync(IncidentRecord record, CancellationToken cancellationToken)
    {
        try
        {
            await _freeze.ReleaseAsync(record.Id, cancellationToken).ConfigureAwait(false);
        }
        catch (IOException)
        {
            return new IncidentResult(true, "freeze_release_pending", record);
        }

        IncidentRecord next = record with { FreezeReleased = true, UpdatedAt = _time.GetUtcNow(), Sequence = record.Sequence + 1 };
        string? failure = await CommitAsync(next, IncidentJournalKinds.FreezeReleased, SystemActor, "released", cancellationToken).ConfigureAwait(false);
        return failure is null ? new IncidentResult(true, "ok", next) : new IncidentResult(true, "freeze_release_pending", record);
    }

    private async ValueTask<(string? Code, string? ApprovalId)> RequireApprovalAsync(
        string runId, string step, string operation, string digest, string control, bool stepUp, CancellationToken cancellationToken)
    {
        ApprovalRequest request = new(runId, _gateRunbook, 1, step, operation, digest, RunbookRisk.High, stepUp, false, control);
        ApprovalResult result;
        try
        {
            result = await _gate.RequestAsync(request, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            return ("approval_unavailable", null);
        }

        if (result is null || !Enum.IsDefined(result.Decision) || !string.Equals(result.RequestDigest, digest, StringComparison.Ordinal))
        {
            return ("approval_mismatch", null);
        }

        string? approvalId = IncidentText.IsIdentifier(result.ApprovalId) ? result.ApprovalId : null;
        return result.Decision switch
        {
            ApprovalDecision.Approved when approvalId is null => ("approval_mismatch", null),
            ApprovalDecision.Approved when stepUp && !result.StepUpSatisfied => ("step_up_missing", approvalId),
            ApprovalDecision.Approved => (null, approvalId),
            ApprovalDecision.Pending => ("approval_pending", approvalId),
            ApprovalDecision.Denied => ("approval_denied", approvalId),
            _ => ("approval_timed_out", approvalId),
        };
    }

    private async ValueTask<string?> CommitAsync(IncidentRecord record, string kind, string actor, string detail, CancellationToken cancellationToken)
    {
        try
        {
            await _journal.AppendAsync(new IncidentJournalEntry(record.Id, record.Sequence, record.UpdatedAt, kind, actor, detail), cancellationToken)
                .ConfigureAwait(false);
        }
        catch (IOException)
        {
            return "journal_unavailable";
        }

        try
        {
            await _store.SaveAsync(record, cancellationToken).ConfigureAwait(false);
        }
        catch (IOException)
        {
            return "state_not_saved";
        }

        return null;
    }

    private async ValueTask TryJournalAsync(IncidentRecord record, string kind, string actor, string detail, CancellationToken cancellationToken)
    {
        try
        {
            await _journal.AppendAsync(new IncidentJournalEntry(record.Id, record.Sequence, _time.GetUtcNow(), kind, actor, detail), cancellationToken)
                .ConfigureAwait(false);
        }
        catch (IOException)
        {
            // Best effort: the caller's own change is journalled through CommitAsync, which does fail closed.
        }
    }

    private async ValueTask<AlertRouteResult?> NotifyAsync(IncidentRecord record, IncidentNotice notice, CancellationToken cancellationToken)
    {
        if (_notifier is null)
        {
            return null;
        }

        try
        {
            return await _notifier.NotifyAsync(record, notice, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            // A notification must never undo or block the incident itself.
            return null;
        }
    }

    private static IncidentRecord Step(IncidentRecord record, IncidentPhase to, DateTimeOffset now, string actor, string reason, string? approvalId) =>
        record with
        {
            Phase = to,
            Transitions = [.. record.Transitions, new IncidentTransition(record.Transitions.Count, record.Phase, to, now, actor, reason, approvalId)],
            UpdatedAt = now,
            Sequence = record.Sequence + 1,
        };

    private static string Digest(params string[] parts) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(string.Join('|', parts))));

    private static IncidentResult Refused(string code) => new(false, code, null);

    private static string? Problem(IncidentOpenRequest r)
    {
        if (!Enum.IsDefined(r.Trigger) || !Enum.IsDefined(r.Severity) || r.GrantedControls is null || r.Categories is null || r.LinkedAlerts is null)
        {
            return "invalid_request";
        }

        return !IncidentText.IsIdentifier(r.Cause) || !IncidentText.IsTitle(r.Title) || !IncidentText.IsIdentifier(r.Opener)
            || !IncidentText.IsIdentifier(r.Reason)
            || r.Categories.Count > IncidentRecord.MaxCategories || !r.Categories.All(IncidentText.IsComponent)
            || r.LinkedAlerts.Count > IncidentRecord.MaxLinkedAlerts || !r.LinkedAlerts.All(IncidentText.IsIdentifier)
            ? "invalid_request"
            : null;
    }
}
