using Bazaar.Server.Coding.Checks;
using Bazaar.Server.Coding.Workspace;

namespace Bazaar.Server.CodingRunner;

/// <summary>Where a worktree lives in its agent's guest VM and the wire that reaches it (doc 16 <see cref="IGuestChannel"/>).</summary>
/// <param name="Channel">The guest wire for this agent's VM.</param>
/// <param name="GuestRoot">The worktree's absolute path inside the guest. Every command is jailed under it.</param>
public sealed record GuestWorktreeTarget(IGuestChannel Channel, string GuestRoot);

/// <summary>
/// Host-side source of guest targets (doc 17, "Shell and run_checks handlers"). The host binds it; the values never come from the
/// model. A null result means the agent holds no such worktree, and the call is refused.
/// </summary>
public interface IGuestWorktreeSource
{
    /// <summary>Returns the target for the agent's worktree, or null when the key names none for this agent.</summary>
    ValueTask<GuestWorktreeTarget?> ResolveAsync(string agentId, string worktreeKey, CancellationToken cancellationToken);
}

/// <summary>What a check command measures. A failing <see cref="Build"/> stops the run; <see cref="Test"/> output is read for counts.</summary>
public enum CheckToolKind
{
    /// <summary>A clean build. Exactly one is required; its exit code is the record's build exit code.</summary>
    Build,

    /// <summary>A test run whose summary line gives the counts.</summary>
    Test,

    /// <summary>Any other check, such as <c>luau-analyze</c> or <c>selene</c>.</summary>
    Other,
}

/// <summary>One configured check command.</summary>
/// <param name="Tool">A short name used as the key of the per-tool exit codes, such as <c>build</c> or <c>test</c>.</param>
/// <param name="Kind">What the command measures.</param>
/// <param name="Argv">Program and arguments, started in the guest with no shell.</param>
/// <param name="Cwd">Working directory relative to the worktree root.</param>
/// <param name="Timeout">Per-command timeout; null means the handler's default.</param>
public sealed record CheckCommand(string Tool, CheckToolKind Kind, IReadOnlyList<string> Argv, string Cwd = ".", TimeSpan? Timeout = null);

/// <summary>Everything a check run needs that the agent must not choose.</summary>
/// <param name="Commit">The commit under check.</param>
/// <param name="BaseCommit">The base commit, when known.</param>
/// <param name="BaseTestCount">The test count on the base branch; null skips the count gate.</param>
/// <param name="TestCountTolerance">How far the count may drop (<c>harness.test_count_drop_tolerance</c>, default 0).</param>
/// <param name="Commands">The configured commands, in run order.</param>
/// <param name="ChangedFiles">The files this change touches, with old and new text, for the scanners.</param>
/// <param name="AllowedFlagCodes">Flag codes that do not fail the run. Null means <c>test-file-changed</c> only.</param>
public sealed record CheckSubject(
    string Commit,
    string? BaseCommit,
    int? BaseTestCount,
    int TestCountTolerance,
    IReadOnlyList<CheckCommand> Commands,
    IReadOnlyList<ChangedFile> ChangedFiles,
    IReadOnlySet<string>? AllowedFlagCodes = null);

/// <summary>Host-side source of check subjects. A null result means there is nothing to check for this worktree.</summary>
public interface ICheckSubjectSource
{
    /// <summary>Returns the subject for the agent's worktree, or null.</summary>
    ValueTask<CheckSubject?> ResolveAsync(string agentId, string worktreeKey, CancellationToken cancellationToken);
}
