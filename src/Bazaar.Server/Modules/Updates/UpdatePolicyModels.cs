namespace Bazaar.Server.Modules.Updates;

/// <summary>The kind of release a candidate is. Only <see cref="GeneralAvailability"/> can ever be adopted automatically.</summary>
public enum VersionType
{
    GeneralAvailability,
    ReleaseCandidate,
    Beta,
}

/// <summary>What a policy does to the candidates in its scope.</summary>
public enum UpdatePolicyAction
{
    /// <summary>Offer the candidate for automatic adoption (inside a maintenance window when the policy has windows).</summary>
    Auto,

    /// <summary>Offer the candidate for a person to adopt.</summary>
    Manual,

    /// <summary>Hold the candidate until the next maintenance window.</summary>
    Defer,

    /// <summary>Keep the installed version; the candidate is not offered.</summary>
    Pin,

    /// <summary>Skip this candidate; it is not offered.</summary>
    Skip,

    /// <summary>Route the candidate to the owner for approval.</summary>
    RequireApproval,
}

/// <summary>The decision the evaluator returns for one candidate (doc 42 section 10.1).</summary>
public enum UpdateDecision
{
    Auto,
    Manual,
    Deferred,
    Skipped,
    Pinned,
    AwaitingApproval,
    Blocked,
}

/// <summary>The rule that produced a decision. Recorded for audit and for tests; first match wins.</summary>
public enum UpdateRule
{
    /// <summary>A policy pinned the line.</summary>
    PinnedByPolicy,

    /// <summary>A policy skipped the candidate.</summary>
    SkippedByPolicy,

    /// <summary>The candidate is not newer than the installed version on its line.</summary>
    NotNewer,

    /// <summary>The dependency test matrix did not pass for the candidate.</summary>
    MatrixFailed,

    /// <summary>A GA version on a line that is not in use. Only the owner can move a line.</summary>
    NewLineNeedsApproval,

    /// <summary>A beta or release candidate. It is never automatic, whatever a policy says.</summary>
    PreReleaseNotAuto,

    /// <summary>A security patch on a line in use, inside a maintenance window.</summary>
    SecurityPatchInWindow,

    /// <summary>A security patch on a line in use, outside a window or with no window configured.</summary>
    SecurityPatchDeferred,

    /// <summary>The default for everything else: a non-security patch, a new minor, or a version that cannot be classified.</summary>
    DefaultManual,

    /// <summary>A matching policy set the decision.</summary>
    PolicyDecision,
}

/// <summary>One offered update: a module version on a line, with its release facts.</summary>
/// <param name="Line">The major line the version belongs to, for example "18" (a string so that a line is an opaque key).</param>
/// <param name="Version">The candidate version, for example "18.1.2".</param>
/// <param name="IsSecurity">True when the release fixes a security issue.</param>
/// <param name="MatrixPassed">True when the dependency test matrix passed for this candidate.</param>
public sealed record UpdateCandidate(
    string ModuleId,
    string Category,
    string Line,
    string Version,
    VersionType VersionType,
    string Channel,
    bool IsSecurity,
    bool MatrixPassed);

/// <summary>
/// The scope of a policy. A null field matches any candidate. A policy with more fields set is more specific; see
/// <see cref="UpdatePolicyEvaluator"/> for the precedence.
/// </summary>
public sealed record UpdatePolicyScope(
    string? ModuleId = null,
    string? Category = null,
    VersionType? VersionType = null,
    string? Channel = null);

/// <summary>Conditions a candidate must meet for a policy to apply. A null field matches either value.</summary>
public sealed record UpdatePolicyConditions(bool? IsSecurity = null, bool? MatrixPassed = null);

/// <summary>
/// One owner-set update policy. <see cref="Windows"/> is empty when the policy has no windows of its own; the evaluator
/// then uses the module's default windows from <see cref="UpdateContext"/>.
/// </summary>
public sealed record UpdatePolicy(
    UpdatePolicyScope Scope,
    UpdatePolicyConditions Conditions,
    IReadOnlyList<MaintenanceWindow> Windows,
    UpdatePolicyAction Action);

/// <summary>
/// What the evaluator knows about the installation. <see cref="InstalledVersionsByLine"/> maps each line in use to its
/// installed version; a line missing from it is a new line. <see cref="DefaultWindows"/> are the module's maintenance windows
/// when no policy supplies its own.
/// </summary>
public sealed record UpdateContext(
    IReadOnlyDictionary<string, string> InstalledVersionsByLine,
    IReadOnlyList<MaintenanceWindow> DefaultWindows)
{
    /// <summary>A context with nothing installed and no windows.</summary>
    public static UpdateContext Empty { get; } = new(
        new Dictionary<string, string>(StringComparer.Ordinal),
        []);
}

/// <summary>
/// The result of one evaluation. <see cref="NextWindowStart"/> is set for a deferred decision and is null when no window is
/// configured. <see cref="AppliedPolicy"/> is the policy that matched, when one did.
/// </summary>
public sealed record UpdateEvaluation(
    UpdateDecision Decision,
    UpdateRule Rule,
    DateTimeOffset? NextWindowStart,
    UpdatePolicy? AppliedPolicy);
