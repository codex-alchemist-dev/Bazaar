using System.Globalization;
using System.Text;

namespace Bazaar.Server.WhatIf;

/// <summary>
/// The What If state machine (doc 26 section 3). The metric spec is hashed first and a mismatch aborts; each branch then runs its
/// own task list in a sandbox, scored on that fixed spec. A run that finishes leaves one <c>system</c> entry in the main GC, and the
/// report always states its counterfactual limit. A quota or work-life-window refusal aborts only its branch (listed as Aborted in the
/// report); a guard violation aborts the whole run (PROPOSED, doc 26 gap decisions).
/// </summary>
/// <remarks>
/// Branches run one after another: the Work-Life BREAK and OFF windows count against quota, so no parallel burst. Reputation is
/// not written here; the result carries <see cref="WhatIfSettings.ReputationWeight"/> for the ledger to apply.
/// </remarks>
public sealed class WhatIfEngine(
    ISandboxRunner runner,
    IWhatIfRunStore store,
    IWhatIfForkSource forks,
    IWhatIfReportSink sink,
    WhatIfSettings settings,
    TimeProvider? clock = null)
{
    private readonly TimeProvider _clock = clock ?? TimeProvider.System;

    public async Task<WhatIfResult> RunAsync(WhatIfRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ValidateRequest(request);

        // Hash first. Everything after this scores against this hash and nothing else.
        var specHash = request.Spec.Hash();
        var runId = Guid.CreateVersion7();
        var run = new WhatIfRun(
            runId, request.DecisionRef, request.SourceGcId, request.ForkSeq, specHash,
            [.. request.Branches.Select(b => new WhatIfBranchRun(b.Name, b.Tasks, WhatIfRunState.Pending))],
            _clock.GetUtcNow());

        if (request.ExpectedSpecHash is not null && !string.Equals(request.ExpectedSpecHash, specHash, StringComparison.Ordinal))
            return await AbortAsync(run, "The metric spec changed after it was fixed (hash mismatch).", ct).ConfigureAwait(false);

        await store.SaveAsync(run, ct).ConfigureAwait(false);
        var fork = await forks.ForkAsync(request.SourceGcId, request.ForkSeq, ct).ConfigureAwait(false);

        var scored = new List<WhatIfRankedBranch>();
        for (var i = 0; i < request.Branches.Count; i++)
        {
            var branch = request.Branches[i];
            run = WithBranch(run, i, run.Branches[i] with { State = WhatIfRunState.Running });
            await store.SaveAsync(run, ct).ConfigureAwait(false);

            var sandbox = new SandboxRequest(
                runId, branch.Name, fork.SandboxGcId, branch.Tasks, specHash, SandboxProfile.Default,
                PushRef: $"{SandboxRequestGuard.RefPrefix}what-if/{runId:N}/{branch.Name}");
            var violations = SandboxRequestGuard.Check(sandbox);
            if (violations.Count > 0)
                return await AbortAsync(run, "Sandbox request rejected: " + string.Join("; ", violations), ct).ConfigureAwait(false);

            SandboxOutcome outcome;
            try
            {
                outcome = await runner.RunAsync(sandbox, ct).ConfigureAwait(false);
            }
            catch (SandboxQuotaRefusedException ex)
            {
                // A quota or window refusal aborts this branch only; the others still run and are scored.
                run = WithBranch(run, i, run.Branches[i] with { State = WhatIfRunState.Aborted, AbortReason = ex.Message });
                await store.SaveAsync(run, ct).ConfigureAwait(false);
                continue;
            }
            catch (SandboxRequestRejectedException ex)
            {
                // A guard violation (token, egress, non-sandbox ref) aborts the whole run.
                return await AbortAsync(run, ex.Message, ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                run = WithBranch(run, i, run.Branches[i] with { State = WhatIfRunState.Failed, AbortReason = ex.GetType().Name });
                await store.SaveAsync(run, ct).ConfigureAwait(false);
                return await FinishAsync(run, scored, $"Branch '{branch.Name}' failed ({ex.GetType().Name}).", ct).ConfigureAwait(false);
            }

            if (!string.Equals(outcome.MetricsSpecHash, specHash, StringComparison.Ordinal))
                return await AbortAsync(run, $"Branch '{branch.Name}' was scored on a different metric spec (hash mismatch).", ct).ConfigureAwait(false);
            if (!SandboxRequestGuard.IsSandboxRef(outcome.PushedRef))
                return await AbortAsync(run, $"Branch '{branch.Name}' reported a push outside {SandboxRequestGuard.RefPrefix}*.", ct).ConfigureAwait(false);

            var score = MetricScorer.Score(request.Spec, outcome.Measurement);
            var result = new WhatIfBranchResult(outcome.Measurement, score, specHash, outcome.PushedRef);
            run = WithBranch(run, i, run.Branches[i] with { State = WhatIfRunState.Completed, Result = result });
            await store.SaveAsync(run, ct).ConfigureAwait(false);
            scored.Add(new WhatIfRankedBranch(branch.Name, score, outcome.Measurement));
        }

        return await FinishAsync(run, scored, null, ct).ConfigureAwait(false);
    }

    private async Task<WhatIfResult> AbortAsync(WhatIfRun run, string reason, CancellationToken ct)
    {
        // Every branch is aborted and its result dropped: scores from an aborted run are not trustworthy. The derived run state is Aborted.
        var branches = run.Branches
            .Select(b => b with { State = WhatIfRunState.Aborted, AbortReason = reason, Result = null })
            .ToArray();
        run = run with { Branches = branches };
        await store.SaveAsync(run, ct).ConfigureAwait(false);
        var report = BuildReport(run, [], reason, listAborted: false);
        // An abort is reported in the run record and the result only; the main GC gets one entry per finished run.
        return new WhatIfResult(run.Id, run.State, run.MetricsSpecHash, [], WhatIfLimits.Counterfactual, report, settings.ReputationWeight, reason);
    }

    private async Task<WhatIfResult> FinishAsync(WhatIfRun run, List<WhatIfRankedBranch> scored, string? failure, CancellationToken ct)
    {
        var ranking = scored.OrderByDescending(r => r.Score).ThenBy(r => r.Branch, StringComparer.Ordinal).ToArray();
        var report = BuildReport(run, ranking, failure);
        await sink.PostSystemEntryAsync(run.SourceGcId, report, ct).ConfigureAwait(false);
        return new WhatIfResult(run.Id, run.State, run.MetricsSpecHash, ranking, WhatIfLimits.Counterfactual, report, settings.ReputationWeight, failure);
    }

    private string BuildReport(WhatIfRun run, IReadOnlyList<WhatIfRankedBranch> ranking, string? note, bool listAborted = true)
    {
        var sb = new StringBuilder();
        sb.Append(CultureInfo.InvariantCulture, $"What If {run.DecisionRef}: {run.State}. Metric spec {run.MetricsSpecHash[..12]}.").AppendLine();
        if (note is not null)
            sb.AppendLine(note);
        foreach (var r in ranking)
        {
            sb.Append(CultureInfo.InvariantCulture,
                $"- {r.Branch}: score {MetricSpec.Format(r.Score)}, CI {(r.Measurement.CiPassed ? "pass" : "fail")}, diff {r.Measurement.DiffLines} lines, review {r.Measurement.Review}, {r.Measurement.Tokens} tokens.")
                .AppendLine();
        }

        if (listAborted)
        {
            foreach (var b in run.Branches.Where(b => b.State == WhatIfRunState.Aborted))
                sb.Append(CultureInfo.InvariantCulture, $"- {b.Name}: Aborted ({b.AbortReason}).").AppendLine();
        }

        sb.Append(CultureInfo.InvariantCulture, $"Reputation weight {MetricSpec.Format(settings.ReputationWeight)}.").AppendLine();
        sb.Append(WhatIfLimits.Counterfactual);
        return sb.ToString();
    }

    private static WhatIfRun WithBranch(WhatIfRun run, int index, WhatIfBranchRun branch)
    {
        var branches = run.Branches.ToArray();
        branches[index] = branch;
        return run with { Branches = branches };
    }

    private static void ValidateRequest(WhatIfRequest request)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(request.DecisionRef);
        ArgumentNullException.ThrowIfNull(request.Spec);
        ArgumentNullException.ThrowIfNull(request.Branches);
        request.Spec.Validate();
        if (request.Branches.Count < 2)
            throw new ArgumentException("A What If run compares at least two branches.", nameof(request));
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var b in request.Branches)
        {
            if (!IsBranchName(b.Name) || !names.Add(b.Name))
                throw new ArgumentException($"Branch name '{b.Name}' is invalid or repeated (lowercase letters, digits, '-' and '_' only).", nameof(request));
            if (b.Tasks is null || b.Tasks.Count == 0 || b.Tasks.Any(string.IsNullOrWhiteSpace))
                throw new ArgumentException($"Branch '{b.Name}' needs a task list with no blank entries.", nameof(request));
        }
    }

    private static bool IsBranchName(string? name) =>
        !string.IsNullOrEmpty(name) && name.Length <= 64 && name.All(c => c is (>= 'a' and <= 'z') or (>= '0' and <= '9') or '-' or '_');
}
