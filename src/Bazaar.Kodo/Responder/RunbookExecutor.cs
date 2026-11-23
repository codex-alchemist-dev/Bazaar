using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Bazaar.Kodo.Evaluator;
using Bazaar.Kodo.Export;

namespace Bazaar.Kodo.Responder;

/// <summary>
/// The runbook engine (doc 46 section 16.2): a deterministic state machine that starts a signed runbook, runs its steps one at a time,
/// journals each step before and after, persists its state after every change, and stops as needs-human on any doubt. It does no I/O
/// itself: steps, approvals, the journal and the state store are all interfaces, and time comes from the <see cref="TimeProvider"/>.
/// </summary>
/// <remarks>
/// A run advances until it must wait (a retry delay, a wait step, an approval) and then returns. Call
/// <see cref="ResumeAsync"/> later, for example after a restart, to continue from the persisted state. An action step that was
/// started but whose result was never recorded is never run again: the engine cannot know what it changed, so the run stops for a human.
/// </remarks>
public sealed class RunbookExecutor
{
    private const int _maxIterations = 256;

    private readonly RunbookCatalogue _catalogue;
    private readonly IRunbookActionRunner _runner;
    private readonly IRunbookJournal _journal;
    private readonly IRunbookStateStore _store;
    private readonly IApprovalGate _gate;
    private readonly RunbookOptions _options;
    private readonly TimeProvider _time;
    private readonly IReadOnlySet<UnattendedRecovery> _unattendedRecoveries;

    /// <summary>Creates the engine. The options are validated.</summary>
    public RunbookExecutor(
        RunbookCatalogue catalogue,
        IRunbookActionRunner runner,
        IRunbookJournal journal,
        IRunbookStateStore store,
        IApprovalGate gate,
        RunbookOptions options,
        TimeProvider timeProvider)
    {
        ArgumentNullException.ThrowIfNull(catalogue);
        ArgumentNullException.ThrowIfNull(runner);
        ArgumentNullException.ThrowIfNull(journal);
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(gate);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(timeProvider);
        options.Validate();
        _unattendedRecoveries = UnattendedRecoveries.Parse(options.UnattendedRecoveries);
        _catalogue = catalogue;
        _runner = runner;
        _journal = journal;
        _store = store;
        _gate = gate;
        _options = options;
        _time = timeProvider;
    }

    /// <summary>Starts a run and advances it as far as it can go. A refusal changes nothing and is journalled best effort.</summary>
    public async ValueTask<RunbookStartResult> StartAsync(RunbookStartRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        DateTimeOffset now = _time.GetUtcNow();

        string? refusal = Refusal(request);
        CatalogueEntry? entry = null;
        if (refusal is null && !_catalogue.TryGet(request.RunbookId, request.RunbookVersion, out entry))
        {
            refusal = "unknown_runbook";
        }

        if (refusal is null && request.ExpectedDigest is not null && !string.Equals(request.ExpectedDigest, entry!.Digest, StringComparison.Ordinal))
        {
            refusal = "tampered_runbook";
        }

        if (refusal is null && !request.GrantedControls.Contains(entry!.Definition.RequiredControl))
        {
            refusal = "missing_control";
        }

        if (refusal is null)
        {
            try
            {
                if (await _store.LoadAsync(request.RunId, cancellationToken).ConfigureAwait(false) is not null)
                {
                    refusal = "duplicate_run";
                }
            }
            catch (IOException)
            {
                refusal = "state_not_readable";
            }
        }

        if (refusal is not null)
        {
            await TryJournalAsync(new RunbookJournalEntry(SafeId(request.RunId), 0, now, RunbookJournalKinds.RunRefused, null, refusal), cancellationToken)
                .ConfigureAwait(false);
            return new RunbookStartResult(false, refusal, null);
        }

        RunbookDefinition definition = entry!.Definition;

        // Two ways to run without approval, both only for untainted input and a run that starts healthy. Frozen and recovering starts were
        // refused above, so an open incident never reaches here. The signature, retry cap and verify step apply to both.
        //  1. The operator named a low-risk, no-approval runbook in AutoRunbookIds (unchanged).
        //  2. The owner-approved setting kodo.recover.unattended names the shipped host restart or slot switch. Those two are Medium risk
        //     with a step-up tier, so rule 1 can never match them. The match is by canonical digest of the shipped definition (a custom
        //     runbook with the same id does not match), and only these two actions are in the enum, so isolate, rollback and every other
        //     risk class stay gated whatever the setting says.
        bool healthyClean = !request.InputsTainted && request.State == KodoState.Healthy;
        bool listed = definition.Risk == RunbookRisk.Low
            && definition.ApprovalTier == RunbookApprovalTier.None
            && _options.AutoRunbookIds.Contains(definition.Id);
        bool enabledByRecoverySetting = OneClickFixes.RecoveryOf(entry) is { } recovery && _unattendedRecoveries.Contains(recovery);
        bool unattended = healthyClean && (listed || enabledByRecoverySetting);

        RunbookRunState state = new(
            RunbookRunState.CurrentVersion,
            request.RunId,
            definition.Id,
            definition.Version,
            entry.Digest,
            RunbookPhase.Running,
            null,
            definition.Preconditions.Count > 0 ? RunbookSection.Preconditions : RunbookSection.Steps,
            0,
            0,
            false,
            null,
            null,
            Gated: !unattended,
            Tainted: request.InputsTainted,
            ActionsAttempted: false,
            AfterRollback: RunbookPhase.RolledBack,
            request.Starter,
            [],
            new Dictionary<string, string>(request.Inputs, StringComparer.Ordinal),
            now,
            now,
            0);

        try
        {
            await _journal.AppendAsync(new RunbookJournalEntry(state.RunId, 0, now, RunbookJournalKinds.RunStarted, null, unattended ? "auto" : "gated"), cancellationToken)
                .ConfigureAwait(false);
            await _store.SaveAsync(state, cancellationToken).ConfigureAwait(false);
        }
        catch (IOException)
        {
            return new RunbookStartResult(false, "state_not_saved", null);
        }

        return new RunbookStartResult(true, null, await AdvanceAsync(state, cancellationToken).ConfigureAwait(false));
    }

    /// <summary>
    /// Continues a persisted run, for example after a restart or when a wait, retry delay or approval is due. Returns null when no such
    /// run is stored. A final run is returned as it is.
    /// </summary>
    public async ValueTask<RunbookRunState?> ResumeAsync(string runId, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrEmpty(runId);
        RunbookRunState? state;
        try
        {
            state = await _store.LoadAsync(runId, cancellationToken).ConfigureAwait(false);
        }
        catch (IOException)
        {
            return null;
        }

        return state is null ? null : await AdvanceAsync(state, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// The dry-run preview of a runbook's action steps (section 16.2). It changes nothing. Returns null for an unknown runbook.
    /// </summary>
    public async ValueTask<IReadOnlyList<RunbookPreview>?> PreviewAsync(
        string runbookId, int version, IReadOnlyDictionary<string, string> inputs, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(inputs);
        if (!_catalogue.TryGet(runbookId, version, out CatalogueEntry? entry) || entry is null)
        {
            return null;
        }

        List<RunbookPreview> previews = [];
        foreach (RunbookStep step in entry.Definition.Steps.Where(s => s.Kind == RunbookStepKind.Action))
        {
            previews.Add(await _runner.PreviewAsync(
                Request($"preview-{runbookId}", runbookId, step, inputs, 1), cancellationToken).ConfigureAwait(false));
        }

        return previews;
    }

    private async ValueTask<RunbookRunState> AdvanceAsync(RunbookRunState state, CancellationToken cancellationToken)
    {
        if (state.IsFinal)
        {
            return state;
        }

        if (!_catalogue.TryGet(state.RunbookId, state.RunbookVersion, out CatalogueEntry? entry) || entry is null)
        {
            return await TerminalAsync(state, RunbookPhase.NeedsHuman, "runbook_unknown", cancellationToken).ConfigureAwait(false);
        }

        if (!string.Equals(entry.Digest, state.Digest, StringComparison.Ordinal))
        {
            return await TerminalAsync(state, RunbookPhase.NeedsHuman, "runbook_tampered", cancellationToken).ConfigureAwait(false);
        }

        RunbookDefinition definition = entry.Definition;
        for (int iteration = 0; iteration < _maxIterations; iteration++)
        {
            DateTimeOffset now = _time.GetUtcNow();
            if (state.NotBefore is { } notBefore && now < notBefore)
            {
                return state;
            }

            IReadOnlyList<RunbookStep> list = ListOf(definition, state.Section);
            if (state.StepIndex > list.Count || state.StepIndex < 0)
            {
                return await TerminalAsync(state, RunbookPhase.NeedsHuman, "state_invalid", cancellationToken).ConfigureAwait(false);
            }

            if (state.StepIndex == list.Count)
            {
                state = await FinishSectionAsync(state, cancellationToken).ConfigureAwait(false);
                if (state.IsFinal)
                {
                    return state;
                }

                continue;
            }

            RunbookStep step = list[state.StepIndex];
            if (state.InFlight)
            {
                if (step.Kind == RunbookStepKind.Action)
                {
                    return await TerminalAsync(state, RunbookPhase.NeedsHuman, "interrupted_action", cancellationToken).ConfigureAwait(false);
                }

                state = state with { InFlight = false };
            }

            if (step.Kind == RunbookStepKind.Action && (state.Gated || step.RequiresApproval))
            {
                string key = state.Section == RunbookSection.Rollback ? $"rollback:{step.Id}" : step.Id;
                if (!state.Approvals.Any(a => string.Equals(a.StepId, key, StringComparison.Ordinal)))
                {
                    (state, ApprovalNext next) = await ApproveAsync(state, definition, step, key, cancellationToken).ConfigureAwait(false);
                    if (next == ApprovalNext.Return || state.IsFinal)
                    {
                        return state;
                    }

                    if (next == ApprovalNext.Reloop)
                    {
                        continue;
                    }
                }
            }

            state = await ExecuteAsync(state, definition, step, cancellationToken).ConfigureAwait(false);
            if (state.IsFinal)
            {
                return state;
            }
        }

        return await TerminalAsync(state, RunbookPhase.NeedsHuman, "iteration_limit", cancellationToken).ConfigureAwait(false);
    }

    private async ValueTask<(RunbookRunState State, ApprovalNext Next)> ApproveAsync(
        RunbookRunState state, RunbookDefinition definition, RunbookStep step, string key, CancellationToken cancellationToken)
    {
        DateTimeOffset now = _time.GetUtcNow();
        if (state.Phase == RunbookPhase.AwaitingApproval && state.ApprovalRequestedAt is { } asked && now - asked >= _options.ApprovalWait)
        {
            return (await TerminalAsync(state, RunbookPhase.NeedsHuman, "approval_timeout", cancellationToken).ConfigureAwait(false), ApprovalNext.Return);
        }

        bool stepUp = definition.Risk == RunbookRisk.High || definition.ApprovalTier == RunbookApprovalTier.ApprovalStepUp;
        ApprovalRequest request = new(
            state.RunId,
            definition.Id,
            definition.Version,
            key,
            step.Operation,
            RequestDigest(state, key, step),
            definition.Risk,
            stepUp,
            state.Tainted,
            definition.RequiredControl);

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
            return (await TerminalAsync(state, RunbookPhase.NeedsHuman, "approval_gate_error", cancellationToken).ConfigureAwait(false), ApprovalNext.Return);
        }

        if (result is null
            || !string.Equals(result.RequestDigest, request.RequestDigest, StringComparison.Ordinal)
            || !Enum.IsDefined(result.Decision))
        {
            return (await TerminalAsync(state, RunbookPhase.NeedsHuman, "approval_mismatch", cancellationToken).ConfigureAwait(false), ApprovalNext.Return);
        }

        switch (result.Decision)
        {
            case ApprovalDecision.Approved:
                if (string.IsNullOrEmpty(result.ApprovalId) || !KodoExportPatterns.IdentifierValue().IsMatch(result.ApprovalId))
                {
                    return (await TerminalAsync(state, RunbookPhase.NeedsHuman, "approval_mismatch", cancellationToken).ConfigureAwait(false), ApprovalNext.Return);
                }

                if (stepUp && !result.StepUpSatisfied)
                {
                    return (await TerminalAsync(state, RunbookPhase.NeedsHuman, "step_up_missing", cancellationToken).ConfigureAwait(false), ApprovalNext.Return);
                }

                RunbookRunState approved = state with
                {
                    Phase = RunbookPhase.Running,
                    ApprovalRequestedAt = null,
                    Approvals = [.. state.Approvals, new RunbookApprovalRecord(key, result.ApprovalId)],
                };
                return await CommitAsync(approved, RunbookJournalKinds.ApprovalGranted, key, result.ApprovalId, cancellationToken).ConfigureAwait(false) is { } saved
                    ? (saved, ApprovalNext.Execute)
                    : (await TerminalAsync(approved, RunbookPhase.NeedsHuman, "state_not_saved", cancellationToken).ConfigureAwait(false), ApprovalNext.Return);

            case ApprovalDecision.Pending:
                if (state.Phase == RunbookPhase.AwaitingApproval)
                {
                    return (state, ApprovalNext.Return);
                }

                RunbookRunState waiting = state with { Phase = RunbookPhase.AwaitingApproval, ApprovalRequestedAt = now };
                return await CommitAsync(waiting, RunbookJournalKinds.ApprovalRequested, key, null, cancellationToken).ConfigureAwait(false) is { } savedWaiting
                    ? (savedWaiting, ApprovalNext.Return)
                    : (await TerminalAsync(waiting, RunbookPhase.NeedsHuman, "state_not_saved", cancellationToken).ConfigureAwait(false), ApprovalNext.Return);

            case ApprovalDecision.Denied:
                await TryJournalAsync(new RunbookJournalEntry(state.RunId, state.Sequence, now, RunbookJournalKinds.ApprovalDenied, key, null), cancellationToken)
                    .ConfigureAwait(false);
                if (state.Section == RunbookSection.Rollback)
                {
                    return (await TerminalAsync(state, RunbookPhase.NeedsHuman, "rollback_not_approved", cancellationToken).ConfigureAwait(false), ApprovalNext.Return);
                }

                RunbookRunState denied = await BeginRollbackAsync(
                    state with { Phase = RunbookPhase.Running, ApprovalRequestedAt = null }, definition, "approval_denied", RunbookPhase.Denied, cancellationToken)
                    .ConfigureAwait(false);
                return (denied, denied.IsFinal ? ApprovalNext.Return : ApprovalNext.Reloop);

            default:
                return (await TerminalAsync(state, RunbookPhase.NeedsHuman, "approval_timeout", cancellationToken).ConfigureAwait(false), ApprovalNext.Return);
        }
    }

    private async ValueTask<RunbookRunState> ExecuteAsync(
        RunbookRunState state, RunbookDefinition definition, RunbookStep step, CancellationToken cancellationToken)
    {
        DateTimeOffset now = _time.GetUtcNow();

        if (step.Kind == RunbookStepKind.Wait)
        {
            if (state.Attempts == 0)
            {
                RunbookRunState started = state with { Attempts = 1, NotBefore = now + step.WaitFor!.Value };
                return await CommitAsync(started, RunbookJournalKinds.StepStarted, step.Id, "wait", cancellationToken).ConfigureAwait(false)
                    ?? await TerminalAsync(started, RunbookPhase.NeedsHuman, "state_not_saved", cancellationToken).ConfigureAwait(false);
            }

            return await StepDoneAsync(state, step, "waited", cancellationToken).ConfigureAwait(false);
        }

        int attempt = state.Attempts + 1;
        RunbookRunState running = state with
        {
            Attempts = attempt,
            InFlight = true,
            ActionsAttempted = state.ActionsAttempted || (step.Kind == RunbookStepKind.Action && state.Section == RunbookSection.Steps),
        };

        if (!await TryJournalAsync(
                new RunbookJournalEntry(state.RunId, state.Sequence, now, RunbookJournalKinds.StepStarted, step.Id, attempt.ToString(CultureInfo.InvariantCulture)),
                cancellationToken).ConfigureAwait(false))
        {
            return await TerminalAsync(state, RunbookPhase.NeedsHuman, "journal_failed", cancellationToken).ConfigureAwait(false);
        }

        RunbookRunState? persisted = await SaveAsync(running, cancellationToken).ConfigureAwait(false);
        if (persisted is null)
        {
            return await TerminalAsync(state, RunbookPhase.NeedsHuman, "state_not_saved", cancellationToken).ConfigureAwait(false);
        }

        running = persisted;
        RunbookStepResult result = await RunStepAsync(running, step, attempt, cancellationToken).ConfigureAwait(false);
        bool readOnly = step.Kind != RunbookStepKind.Action;
        string detail = SafeDetail(result.Detail);

        switch (result.Outcome)
        {
            case RunbookStepOutcome.Succeeded:
                return await StepDoneAsync(running, step, detail, cancellationToken).ConfigureAwait(false);

            case RunbookStepOutcome.TimedOut when !readOnly:
                await TryJournalAsync(new RunbookJournalEntry(running.RunId, running.Sequence, _time.GetUtcNow(), RunbookJournalKinds.StepFailed, step.Id, "timed_out"), cancellationToken)
                    .ConfigureAwait(false);
                return await TerminalAsync(running, RunbookPhase.NeedsHuman, "action_timed_out", cancellationToken).ConfigureAwait(false);

            case RunbookStepOutcome.Transient or RunbookStepOutcome.TimedOut:
                if (attempt < Math.Min(step.MaxAttempts, _options.MaxAttemptsCap))
                {
                    RunbookRunState retry = running with { InFlight = false, NotBefore = _time.GetUtcNow() + RetryDelay(attempt) };
                    return await CommitAsync(retry, RunbookJournalKinds.StepRetry, step.Id, detail, cancellationToken).ConfigureAwait(false)
                        ?? await TerminalAsync(retry, RunbookPhase.NeedsHuman, "state_not_saved", cancellationToken).ConfigureAwait(false);
                }

                return await StepFailedAsync(running, definition, step, detail, cancellationToken).ConfigureAwait(false);

            default:
                return await StepFailedAsync(running, definition, step, detail, cancellationToken).ConfigureAwait(false);
        }
    }

    private async ValueTask<RunbookStepResult> RunStepAsync(RunbookRunState state, RunbookStep step, int attempt, CancellationToken cancellationToken)
    {
        bool readOnly = step.Kind != RunbookStepKind.Action;
        using CancellationTokenSource timeout = new(step.Timeout, _time);
        using CancellationTokenSource linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeout.Token);
        try
        {
            RunbookStepResult result = await _runner.RunAsync(Request(state.RunId, state.RunbookId, step, state.Inputs, attempt), linked.Token).ConfigureAwait(false);
            return result is not null && Enum.IsDefined(result.Outcome)
                ? result
                : new RunbookStepResult(readOnly ? RunbookStepOutcome.Failed : RunbookStepOutcome.TimedOut, "invalid_result");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            return new RunbookStepResult(RunbookStepOutcome.TimedOut, "timed_out");
        }
        catch (Exception)
        {
            return new RunbookStepResult(readOnly ? RunbookStepOutcome.Transient : RunbookStepOutcome.TimedOut, "runner_error");
        }
    }

    private async ValueTask<RunbookRunState> StepDoneAsync(RunbookRunState state, RunbookStep step, string detail, CancellationToken cancellationToken)
    {
        RunbookRunState next = state with { StepIndex = state.StepIndex + 1, Attempts = 0, InFlight = false, NotBefore = null };
        return await CommitAsync(next, RunbookJournalKinds.StepSucceeded, step.Id, detail, cancellationToken).ConfigureAwait(false)
            ?? await TerminalAsync(next, RunbookPhase.NeedsHuman, "state_not_saved", cancellationToken).ConfigureAwait(false);
    }

    private async ValueTask<RunbookRunState> StepFailedAsync(
        RunbookRunState state, RunbookDefinition definition, RunbookStep step, string detail, CancellationToken cancellationToken)
    {
        await TryJournalAsync(new RunbookJournalEntry(state.RunId, state.Sequence, _time.GetUtcNow(), RunbookJournalKinds.StepFailed, step.Id, detail), cancellationToken)
            .ConfigureAwait(false);

        if (step.Kind == RunbookStepKind.Notify)
        {
            return await StepDoneAsync(state, step, "notify_failed_ignored", cancellationToken).ConfigureAwait(false);
        }

        return state.Section switch
        {
            RunbookSection.Preconditions => await TerminalAsync(state, RunbookPhase.Failed, $"precondition_failed", cancellationToken).ConfigureAwait(false),
            RunbookSection.Steps => await BeginRollbackAsync(state with { InFlight = false }, definition, "step_failed", RunbookPhase.RolledBack, cancellationToken).ConfigureAwait(false),
            _ => await TerminalAsync(state, RunbookPhase.NeedsHuman, "rollback_failed", cancellationToken).ConfigureAwait(false),
        };
    }

    private async ValueTask<RunbookRunState> BeginRollbackAsync(
        RunbookRunState state, RunbookDefinition definition, string cause, RunbookPhase after, CancellationToken cancellationToken)
    {
        if (!state.ActionsAttempted)
        {
            return await TerminalAsync(state, after == RunbookPhase.Denied ? RunbookPhase.Denied : RunbookPhase.Failed, cause, cancellationToken).ConfigureAwait(false);
        }

        // A failed unattended host restart or slot switch (a failed verify included) stops here. Rollback of the slot switch is a roll back
        // and always needs step-up, so it is never run on the setting's authority: a human takes over.
        if (!state.Gated && _catalogue.TryGet(state.RunbookId, state.RunbookVersion, out CatalogueEntry? current)
            && current is not null && OneClickFixes.RecoveryOf(current) is not null)
        {
            return await TerminalAsync(state, RunbookPhase.NeedsHuman, "unattended_recovery_failed", cancellationToken).ConfigureAwait(false);
        }

        if (definition.Rollback.Count == 0)
        {
            return await TerminalAsync(state, RunbookPhase.NeedsHuman, "no_rollback_defined", cancellationToken).ConfigureAwait(false);
        }

        RunbookRunState rolling = state with
        {
            Section = RunbookSection.Rollback,
            StepIndex = 0,
            Attempts = 0,
            InFlight = false,
            NotBefore = null,
            Phase = RunbookPhase.Running,
            Cause = cause,
            AfterRollback = after,
        };
        return await CommitAsync(rolling, RunbookJournalKinds.RollbackStarted, null, cause, cancellationToken).ConfigureAwait(false)
            ?? await TerminalAsync(rolling, RunbookPhase.NeedsHuman, "state_not_saved", cancellationToken).ConfigureAwait(false);
    }

    private async ValueTask<RunbookRunState> FinishSectionAsync(RunbookRunState state, CancellationToken cancellationToken)
    {
        switch (state.Section)
        {
            case RunbookSection.Preconditions:
                RunbookRunState steps = state with { Section = RunbookSection.Steps, StepIndex = 0, Attempts = 0, InFlight = false, NotBefore = null };
                return await SaveAsync(steps, cancellationToken).ConfigureAwait(false)
                    ?? await TerminalAsync(steps, RunbookPhase.NeedsHuman, "state_not_saved", cancellationToken).ConfigureAwait(false);
            case RunbookSection.Steps:
                return await TerminalAsync(state, RunbookPhase.Succeeded, null, cancellationToken).ConfigureAwait(false);
            default:
                return await TerminalAsync(state, state.AfterRollback, state.Cause, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>Ends the run. Journal and save are best effort: a stop must never be blocked by the failure that caused it.</summary>
    private async ValueTask<RunbookRunState> TerminalAsync(RunbookRunState state, RunbookPhase phase, string? cause, CancellationToken cancellationToken)
    {
        DateTimeOffset now = _time.GetUtcNow();
        RunbookRunState final = state with
        {
            Phase = phase,
            Cause = cause ?? state.Cause,
            InFlight = state.InFlight,
            NotBefore = null,
            ApprovalRequestedAt = null,
            UpdatedAt = now,
            Sequence = state.Sequence + 1,
        };

        await TryJournalAsync(new RunbookJournalEntry(final.RunId, final.Sequence, now, RunbookJournalKinds.RunFinished, null, final.Cause ?? phase.ToString().ToLowerInvariant()), cancellationToken)
            .ConfigureAwait(false);
        try
        {
            await _store.SaveAsync(final, cancellationToken).ConfigureAwait(false);
        }
        catch (IOException)
        {
            // The in-memory result still tells the caller to stop; the stored state keeps its in-flight marker, which also stops a resume.
        }

        return final;
    }

    /// <summary>Journals one entry, then saves the state with the next sequence. Null when either failed.</summary>
    private async ValueTask<RunbookRunState?> CommitAsync(
        RunbookRunState state, string kind, string? stepId, string? detail, CancellationToken cancellationToken)
    {
        if (!await TryJournalAsync(new RunbookJournalEntry(state.RunId, state.Sequence, _time.GetUtcNow(), kind, stepId, detail), cancellationToken).ConfigureAwait(false))
        {
            return null;
        }

        return await SaveAsync(state, cancellationToken).ConfigureAwait(false);
    }

    private async ValueTask<RunbookRunState?> SaveAsync(RunbookRunState state, CancellationToken cancellationToken)
    {
        RunbookRunState next = state with { Sequence = state.Sequence + 1, UpdatedAt = _time.GetUtcNow() };
        try
        {
            await _store.SaveAsync(next, cancellationToken).ConfigureAwait(false);
            return next;
        }
        catch (IOException)
        {
            return null;
        }
    }

    private async ValueTask<bool> TryJournalAsync(RunbookJournalEntry entry, CancellationToken cancellationToken)
    {
        try
        {
            await _journal.AppendAsync(entry, cancellationToken).ConfigureAwait(false);
            return true;
        }
        catch (IOException)
        {
            return false;
        }
    }

    private static string? Refusal(RunbookStartRequest request)
    {
        if (string.IsNullOrEmpty(request.RunId) || !KodoExportPatterns.EventId().IsMatch(request.RunId))
        {
            return "invalid_run_id";
        }

        if (string.IsNullOrEmpty(request.Starter) || !KodoExportPatterns.IdentifierValue().IsMatch(request.Starter)
            || request.GrantedControls is null || request.Inputs is null || request.Inputs.Count > 16)
        {
            return "invalid_request";
        }

        foreach (KeyValuePair<string, string> pair in request.Inputs)
        {
            if (pair.Key is null || pair.Value is null
                || !KodoExportPatterns.IdentifierName().IsMatch(pair.Key) || !KodoExportPatterns.IdentifierValue().IsMatch(pair.Value))
            {
                return "invalid_request";
            }
        }

        return request.State switch
        {
            KodoState.Frozen => "incident_frozen",
            KodoState.Recovering => "recovery_running",
            KodoState.Healthy or KodoState.Degraded or KodoState.Failing => null,
            _ => "invalid_request",
        };
    }

    private TimeSpan RetryDelay(int attempt)
    {
        double seconds = _options.RetryBaseDelay.TotalSeconds * Math.Pow(2, attempt - 1);
        return TimeSpan.FromSeconds(Math.Min(seconds, _options.RetryMaxDelay.TotalSeconds));
    }

    private static RunbookStepRequest Request(
        string runId, string runbookId, RunbookStep step, IReadOnlyDictionary<string, string> inputs, int attempt) =>
        new(runId, runbookId, step.Id, step.Kind, step.Operation, step.Parameters, inputs, $"{runId}/{step.Id}", attempt, step.Timeout);

    private static string RequestDigest(RunbookRunState state, string key, RunbookStep step)
    {
        StringBuilder text = new();
        text.Append(state.RunId).Append('|').Append(state.Digest).Append('|').Append(key).Append('|').Append(step.Operation);
        foreach (KeyValuePair<string, string> pair in step.Parameters.OrderBy(entry => entry.Key, StringComparer.Ordinal))
        {
            text.Append('|').Append(pair.Key).Append('=').Append(pair.Value);
        }

        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text.ToString())));
    }

    private static IReadOnlyList<RunbookStep> ListOf(RunbookDefinition definition, RunbookSection section) => section switch
    {
        RunbookSection.Preconditions => definition.Preconditions,
        RunbookSection.Steps => definition.Steps,
        _ => definition.Rollback,
    };

    private static string SafeDetail(string? detail) =>
        !string.IsNullOrEmpty(detail) && KodoExportPatterns.IdentifierValue().IsMatch(detail) ? detail : "unspecified";

    private static string SafeId(string? id) =>
        !string.IsNullOrEmpty(id) && KodoExportPatterns.EventId().IsMatch(id) ? id : "invalid";

    private enum ApprovalNext
    {
        Execute,
        Return,
        Reloop,
    }
}
