using Bazaar.Kodo.Evaluator;
using Bazaar.Kodo.Responder;

namespace Bazaar.Kodo.Incidents;

/// <summary>
/// Maps an incident cause to the runbooks that help (doc 46 section 16.3 item 5). It reuses the Responder's <see cref="OneClickFixes"/>
/// (a probe id to fix ids) and its signed <see cref="RunbookCatalogue"/>, and adds only the trigger floor and the operator's own mapping.
/// </summary>
public sealed class IncidentRecoveryPlanner
{
    /// <summary>Runbooks suggested for the automatic triggers whose cause is not a single probe (section 16.5). Split-brain has none: fencing is a human decision.</summary>
    public static IReadOnlyDictionary<IncidentTrigger, IReadOnlyList<string>> TriggerRunbooks { get; } =
        new Dictionary<IncidentTrigger, IReadOnlyList<string>>
        {
            [IncidentTrigger.IntegrityFailure] = ["fix.slot_switch"],
            [IncidentTrigger.ReplicationBreak] = ["fix.replication_reseed"],
        };

    private readonly RunbookCatalogue _catalogue;
    private readonly IncidentOptions _options;

    /// <summary>Creates a planner. The options are validated.</summary>
    public IncidentRecoveryPlanner(RunbookCatalogue catalogue, IncidentOptions options)
    {
        ArgumentNullException.ThrowIfNull(catalogue);
        ArgumentNullException.ThrowIfNull(options);
        options.Validate();
        _catalogue = catalogue;
        _options = options;
    }

    /// <summary>
    /// The ordered plan for a trigger and cause: the trigger floor, the shipped fixes for the cause's probe id (any scope after a colon is
    /// ignored), then the operator's mapping. Each runbook appears once, in first-seen order, up to <see cref="IncidentRecord.MaxPlan"/>.
    /// </summary>
    public IReadOnlyList<PlannedRunbook> Plan(IncidentTrigger trigger, string cause)
    {
        ArgumentException.ThrowIfNullOrEmpty(cause);
        int colon = cause.IndexOf(':', StringComparison.Ordinal);
        string probe = colon > 0 ? cause[..colon] : cause;

        List<string> ids = [];
        if (TriggerRunbooks.TryGetValue(trigger, out IReadOnlyList<string>? floor))
        {
            ids.AddRange(floor);
        }

        ids.AddRange(OneClickFixes.For(probe).Select(fix => fix.Id));
        if (_options.CauseRunbooks.TryGetValue(probe, out IReadOnlyList<string>? extra) && extra is not null)
        {
            ids.AddRange(extra);
        }

        List<PlannedRunbook> plan = [];
        foreach (string id in ids.Where(IncidentText.IsComponent).Distinct(StringComparer.Ordinal).Take(IncidentRecord.MaxPlan))
        {
            plan.Add(Resolve(id));
        }

        return plan;
    }

    private PlannedRunbook Resolve(string id)
    {
        CatalogueEntry? signed = _catalogue.Latest(id);
        if (signed is not null)
        {
            return new PlannedRunbook(id, signed.Definition.Version, Clip(signed.Definition.Title), true);
        }

        OneClickFix? shipped = OneClickFixes.All.FirstOrDefault(fix => string.Equals(fix.Id, id, StringComparison.Ordinal));
        return shipped is null
            ? new PlannedRunbook(id, 1, id, false)
            : new PlannedRunbook(id, shipped.Definition.Version, Clip(shipped.Title), false);
    }

    private static string Clip(string title) => title.Length <= RunbookDefinition.MaxTitleLength ? title : title[..RunbookDefinition.MaxTitleLength];
}

/// <summary>One line of the guided-recovery checklist.</summary>
/// <param name="RunbookId">The runbook.</param>
/// <param name="Version">Its version.</param>
/// <param name="Title">Its title.</param>
/// <param name="Available">False when the catalogue holds no signed copy, so it cannot run yet.</param>
/// <param name="RunId">The run, or null when not started. A suggestion until a role with the control starts it.</param>
/// <param name="Phase">The run's phase, or null when not started.</param>
public sealed record IncidentChecklistItem(string RunbookId, int Version, string Title, bool Available, string? RunId, RunbookPhase? Phase);

/// <summary>A request to start one of the incident's runbooks.</summary>
/// <param name="IncidentId">The incident.</param>
/// <param name="RunbookId">The runbook; it must be in the incident's plan.</param>
/// <param name="Starter">Who starts it, as an identifier.</param>
/// <param name="GrantedControls">The controls the starter holds. The runbook's own control is checked by the Responder.</param>
/// <param name="Inputs">Identifier inputs for the runner.</param>
/// <param name="InputsTainted">True when any input came from untrusted content (section 16.2).</param>
/// <param name="Underlying">The underlying health state from the evaluator. It is never <see cref="KodoState.Frozen"/>.</param>
public sealed record IncidentRecoveryRequest(
    string IncidentId,
    string RunbookId,
    string Starter,
    IReadOnlySet<string> GrantedControls,
    IReadOnlyDictionary<string, string> Inputs,
    bool InputsTainted,
    KodoState Underlying);

/// <summary>The answer to a recovery start.</summary>
/// <param name="Started">True when the Responder started the run.</param>
/// <param name="Code">A fixed code: <c>started</c>, an incident code, or the Responder's refusal code.</param>
/// <param name="RunId">The run id when a run was created.</param>
/// <param name="Incident">The incident after the call.</param>
public sealed record IncidentRecoveryResult(bool Started, string Code, string? RunId, IncidentRecord? Incident);

/// <summary>
/// Drives the incident's runbooks through the Responder's <see cref="RunbookExecutor"/> (section 16.3 item 5). Recovery steps are only
/// suggestions until a role with the runbook's control starts one. Only runbooks in the incident's plan may start, and never unattended.
/// </summary>
public sealed class IncidentRecoveryDriver
{
    private readonly IncidentController _controller;
    private readonly RunbookExecutor _executor;

    /// <summary>Creates the driver.</summary>
    public IncidentRecoveryDriver(IncidentController controller, RunbookExecutor executor)
    {
        ArgumentNullException.ThrowIfNull(controller);
        ArgumentNullException.ThrowIfNull(executor);
        _controller = controller;
        _executor = executor;
    }

    /// <summary>The checklist: the plan in order, each with the latest run linked to it, if any.</summary>
    public static IReadOnlyList<IncidentChecklistItem> Checklist(IncidentRecord incident)
    {
        ArgumentNullException.ThrowIfNull(incident);
        return incident.Plan
            .Select(planned =>
            {
                IncidentRunLink? last = incident.Runs.LastOrDefault(run => string.Equals(run.RunbookId, planned.RunbookId, StringComparison.Ordinal));
                return new IncidentChecklistItem(planned.RunbookId, planned.Version, planned.Title, planned.Available, last?.RunId, last?.Phase);
            })
            .ToList();
    }

    /// <summary>Starts one planned runbook. The Responder checks the signature, the control, taint and approvals.</summary>
    public async ValueTask<IncidentRecoveryResult> StartAsync(IncidentRecoveryRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        IncidentRecord? incident = await _controller.GetAsync(request.IncidentId, cancellationToken).ConfigureAwait(false);
        if (incident is null)
        {
            return new IncidentRecoveryResult(false, "unknown_incident", null, null);
        }

        if (incident.Phase is not (IncidentPhase.Investigating or IncidentPhase.Recovering))
        {
            return new IncidentRecoveryResult(false, "not_in_recovery_phase", null, incident);
        }

        PlannedRunbook? planned = incident.Plan.FirstOrDefault(p => string.Equals(p.RunbookId, request.RunbookId, StringComparison.Ordinal));
        if (planned is null)
        {
            return new IncidentRecoveryResult(false, "not_in_plan", null, incident);
        }

        if (incident.Runs.Count >= IncidentRecord.MaxRuns)
        {
            return new IncidentRecoveryResult(false, "run_limit", null, incident);
        }

        // The Responder refuses starts while the displayed state is frozen (section 16.8). The incident's own runbooks are the exception, so
        // the underlying state is passed. Healthy and degraded count as failing: a run during an incident is never unattended.
        KodoState state = request.Underlying switch
        {
            KodoState.Frozen => KodoState.Frozen,
            KodoState.Recovering => KodoState.Recovering,
            _ => KodoState.Failing,
        };
        if (state == KodoState.Frozen)
        {
            return new IncidentRecoveryResult(false, "invalid_state", null, incident);
        }

        string runId = $"{incident.Id}-r{incident.Runs.Count + 1}";
        RunbookStartResult started = await _executor.StartAsync(
            new RunbookStartRequest(
                runId, planned.RunbookId, planned.Version, null, request.Starter, request.GrantedControls, request.Inputs, request.InputsTainted, state),
            cancellationToken).ConfigureAwait(false);

        if (!started.Started)
        {
            return new IncidentRecoveryResult(false, started.Refusal ?? "refused", null, incident);
        }

        IncidentResult linked = await _controller.LinkRunAsync(
            incident.Id, new IncidentRunLink(runId, planned.RunbookId, started.State!.Phase), request.Starter, cancellationToken).ConfigureAwait(false);
        return linked.Ok
            ? new IncidentRecoveryResult(true, "started", runId, linked.Incident)
            : new IncidentRecoveryResult(true, "run_not_linked", runId, incident);
    }

    /// <summary>
    /// Polls the linked runs through the Responder (it advances a run that is due and returns the state), records their phases, and moves a
    /// recovering incident back to investigating when a run ended without success and none is still going.
    /// </summary>
    public async ValueTask<IReadOnlyList<IncidentChecklistItem>?> RefreshAsync(string incidentId, CancellationToken cancellationToken)
    {
        IncidentRecord? incident = await _controller.GetAsync(incidentId, cancellationToken).ConfigureAwait(false);
        if (incident is null)
        {
            return null;
        }

        foreach (IncidentRunLink link in incident.Runs.Where(run => run.Phase is RunbookPhase.Running or RunbookPhase.AwaitingApproval))
        {
            RunbookRunState? state = await _executor.ResumeAsync(link.RunId, cancellationToken).ConfigureAwait(false);
            if (state is not null && state.Phase != link.Phase)
            {
                IncidentResult updated = await _controller.UpdateRunAsync(incident.Id, link with { Phase = state.Phase }, cancellationToken).ConfigureAwait(false);
                if (updated.Ok)
                {
                    incident = updated.Incident!;
                }
            }
        }

        bool anyActive = incident.Runs.Any(run => run.Phase is RunbookPhase.Running or RunbookPhase.AwaitingApproval);
        bool anySucceeded = incident.Runs.Any(run => run.Phase == RunbookPhase.Succeeded);
        if (incident.Phase == IncidentPhase.Recovering && !anyActive && !anySucceeded)
        {
            IncidentResult back = await _controller.AdvanceAsync(
                new IncidentAdvanceRequest(incident.Id, IncidentPhase.Investigating, IncidentController.SystemActor, "run_failed", new HashSet<string>(StringComparer.Ordinal), null),
                cancellationToken).ConfigureAwait(false);
            if (back.Ok)
            {
                incident = back.Incident!;
            }
        }

        return Checklist(incident);
    }
}
