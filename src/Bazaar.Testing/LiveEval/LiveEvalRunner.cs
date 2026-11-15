namespace Bazaar.Testing.LiveEval;

/// <summary>
/// The seam a live eval runs against. An implementation drives one model or gateway source, and must not depend on
/// Bazaar.Server. Each check kind the case declares needs an entry in the observation; a missing entry fails the check.
/// </summary>
public interface ILiveEvalTarget
{
    /// <summary>A label for reports, such as a model id. It is not a secret.</summary>
    string TargetId { get; }

    /// <summary>Runs one attempt of one case and reports which checks were satisfied.</summary>
    Task<LiveEvalObservation> RunCaseAsync(TaskCase taskCase, int seed, CancellationToken cancellationToken);
}

/// <summary>What a target observed for one attempt: each check kind mapped to whether it was satisfied.</summary>
public sealed record LiveEvalObservation(IReadOnlyDictionary<TaskCheckKind, bool> CheckResults);

/// <summary>The process environment and paths a live eval would run with, as the credential guard needs them.</summary>
public sealed record LiveEvalRunEnvironment(
    IReadOnlyDictionary<string, string?> Variables,
    string RepoRoot,
    string WorkingDirectory);

/// <summary>Pass or fail for one case, across all of its attempts. Failure texts name checks and exception types only.</summary>
public sealed record LiveEvalCaseResult(string CaseId, bool Passed, int Attempts, IReadOnlyList<string> Failures);

/// <summary>Per-case results for one task set against one target.</summary>
public sealed record LiveEvalReport(string TaskSetId, string TargetId, IReadOnlyList<LiveEvalCaseResult> Cases)
{
    public int PassedCount => Cases.Count(result => result.Passed);

    public bool AllPassed => Cases.All(result => result.Passed);
}

/// <summary>Thrown when the credential guard refuses to start a live eval. It carries findings, never values.</summary>
public sealed class LiveEvalRefusedException(IReadOnlyList<CredentialFinding> findings)
    : Exception($"Live eval refused by the credential guard ({findings.Count} finding(s)): " + string.Join("; ", findings))
{
    public IReadOnlyList<CredentialFinding> Findings { get; } = findings;
}

/// <summary>
/// Runs a task set against one target. The credential guard runs first; a finding stops the run before the target is
/// called. Each case runs <see cref="LiveEvalOptions.SeedCount"/> attempts, and a case passes only if all of them pass.
/// </summary>
public sealed class LiveEvalRunner(ILiveEvalTarget target, LiveEvalOptions options)
{
    public async Task<LiveEvalReport> RunAsync(
        TaskSet taskSet,
        LiveEvalRunEnvironment environment,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(taskSet);
        ArgumentNullException.ThrowIfNull(environment);
        options.Validate();

        var findings = LiveEvalCredentialGuard.Scan(
            taskSet, environment.Variables, environment.RepoRoot, environment.WorkingDirectory);
        if (findings.Count > 0)
        {
            throw new LiveEvalRefusedException(findings);
        }

        var results = new List<LiveEvalCaseResult>(taskSet.Cases.Count);
        foreach (var taskCase in taskSet.Cases)
        {
            results.Add(await RunCaseAsync(taskCase, cancellationToken));
        }

        return new LiveEvalReport(taskSet.Id, target.TargetId, results);
    }

    private async Task<LiveEvalCaseResult> RunCaseAsync(TaskCase taskCase, CancellationToken cancellationToken)
    {
        var failures = new List<string>();
        for (var seed = 0; seed < options.SeedCount; seed++)
        {
            LiveEvalObservation observation;
            try
            {
                observation = await target.RunCaseAsync(taskCase, seed, cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                failures.Add($"seed {seed}: target threw {ex.GetType().Name}");
                continue;
            }

            foreach (var kind in taskCase.Checks.Select(check => check.Kind).Distinct())
            {
                if (!observation.CheckResults.TryGetValue(kind, out var satisfied))
                {
                    failures.Add($"seed {seed}: check '{TaskSetLoader.ToWireName(kind)}' not observed");
                }
                else if (!satisfied)
                {
                    failures.Add($"seed {seed}: check '{TaskSetLoader.ToWireName(kind)}' not satisfied");
                }
            }
        }

        return new LiveEvalCaseResult(taskCase.Id, failures.Count == 0, options.SeedCount, failures);
    }
}
