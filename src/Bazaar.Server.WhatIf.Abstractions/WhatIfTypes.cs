namespace Bazaar.Server.WhatIf;

/// <summary>Where a run, or one of its branches, stands. A run's own state is derived from its branches (<see cref="WhatIfRun.State"/>).</summary>
public enum WhatIfRunState
{
    Pending,
    Running,
    Completed,

    /// <summary>Stopped before or during execution by a guard: a metric spec mismatch or a rejected sandbox request.</summary>
    Aborted,

    /// <summary>The runner threw or could not finish.</summary>
    Failed,
}

/// <summary>A reviewer's verdict band. Doc 26 names "review band" as a metric but not its levels; these three are a placeholder (see doc gaps).</summary>
public enum ReviewBand
{
    Low = 0,
    Medium = 1,
    High = 2,
}

/// <summary>What a branch produced, before scoring.</summary>
public sealed record BranchMeasurement(bool CiPassed, int DiffLines, ReviewBand Review, long Tokens);

/// <summary>A branch's measurement scored on the fixed spec. Score is 0 to 1.</summary>
public sealed record WhatIfBranchResult(BranchMeasurement Measurement, decimal Score, string MetricsSpecHash, string PushedRef);

/// <summary>One alternative: a name and the task list it runs.</summary>
public sealed record WhatIfBranch(string Name, IReadOnlyList<string> Tasks);

/// <summary>One branch of a stored run (one <c>what_if_run</c> row).</summary>
public sealed record WhatIfBranchRun(
    string Name,
    IReadOnlyList<string> Tasks,
    WhatIfRunState State,
    WhatIfBranchResult? Result = null,
    string? AbortReason = null);

/// <summary>
/// A run as stored. <see cref="State"/> is derived from the branches so the two cannot disagree: any aborted branch makes the
/// run Aborted, then any failed branch Failed, then any running branch Running, all completed Completed, otherwise Pending.
/// </summary>
public sealed record WhatIfRun(
    Guid Id,
    string DecisionRef,
    Guid SourceGcId,
    long ForkSeq,
    string MetricsSpecHash,
    IReadOnlyList<WhatIfBranchRun> Branches,
    DateTimeOffset CreatedAt)
{
    public WhatIfRunState State =>
        Branches.Any(b => b.State == WhatIfRunState.Aborted) ? WhatIfRunState.Aborted
        : Branches.Any(b => b.State == WhatIfRunState.Failed) ? WhatIfRunState.Failed
        : Branches.Any(b => b.State == WhatIfRunState.Running) ? WhatIfRunState.Running
        : Branches.Count > 0 && Branches.All(b => b.State == WhatIfRunState.Completed) ? WhatIfRunState.Completed
        : WhatIfRunState.Pending;

    public string? AbortReason => Branches.Select(b => b.AbortReason).FirstOrDefault(r => r is not null);
}

/// <summary>What the caller asks for. <see cref="ExpectedSpecHash"/> is the hash recorded when the spec was agreed; null means "hash it now".</summary>
public sealed record WhatIfRequest(
    string DecisionRef,
    Guid SourceGcId,
    long ForkSeq,
    MetricSpec Spec,
    IReadOnlyList<WhatIfBranch> Branches,
    string? ExpectedSpecHash = null);

/// <summary>One ranked branch in the report.</summary>
public sealed record WhatIfRankedBranch(string Branch, decimal Score, BranchMeasurement Measurement);

/// <summary>The outcome of a run. <see cref="Limitation"/> is always set and always states the counterfactual limit.</summary>
public sealed record WhatIfResult(
    Guid RunId,
    WhatIfRunState State,
    string MetricsSpecHash,
    IReadOnlyList<WhatIfRankedBranch> Ranking,
    string Limitation,
    string Report,
    decimal ReputationWeight,
    string? AbortReason = null);

/// <summary>The fixed limit text every report carries (doc 26 section 3).</summary>
public static class WhatIfLimits
{
    public const string Counterfactual =
        "Limit: this is a counterfactual. Each branch ran in isolation, so it ignores how other agents and the owner would have reacted.";
}
