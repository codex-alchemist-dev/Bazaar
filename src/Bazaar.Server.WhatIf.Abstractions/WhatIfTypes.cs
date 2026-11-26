namespace Bazaar.Server.WhatIf;

/// <summary>Where a run, or one of its branches, stands. A run's own state is derived from its branches (<see cref="WhatIfRun.State"/>).</summary>
public enum WhatIfRunState
{
    Pending,
    Running,
    Completed,

    /// <summary>Stopped before or during execution: by a guard (metric spec mismatch, rejected sandbox request) for the whole run, or by a quota or window refusal for one branch.</summary>
    Aborted,

    /// <summary>The runner threw or could not finish.</summary>
    Failed,
}

/// <summary>
/// The reviewer's verdict concern level (PROPOSED, doc 26 gap decisions): <see cref="Low"/> is approve, <see cref="Medium"/> is
/// approve with comments, <see cref="High"/> is changes requested. These are not the approval tiers or reputation bands of other docs.
/// </summary>
public enum ReviewBand
{
    /// <summary>Approve.</summary>
    Low = 0,

    /// <summary>Approve with comments.</summary>
    Medium = 1,

    /// <summary>Changes requested.</summary>
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
/// A run as stored. <see cref="State"/> is derived from the branches so the two cannot disagree: all branches aborted makes the
/// run Aborted (a whole-run abort aborts every branch), then any failed branch Failed, then any running branch Running, then
/// every branch completed or aborted (a quota-refused branch) with at least one completed Completed, otherwise Pending.
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
        Branches.Count > 0 && Branches.All(b => b.State == WhatIfRunState.Aborted) ? WhatIfRunState.Aborted
        : Branches.Any(b => b.State == WhatIfRunState.Failed) ? WhatIfRunState.Failed
        : Branches.Any(b => b.State == WhatIfRunState.Running) ? WhatIfRunState.Running
        : Branches.Any(b => b.State == WhatIfRunState.Completed) && Branches.All(b => b.State is WhatIfRunState.Completed or WhatIfRunState.Aborted) ? WhatIfRunState.Completed
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
