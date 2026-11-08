namespace Bazaar.Contracts.Coding;

/// <summary>
/// One row of the <c>check_runs</c> table: a server-owned verification run on a commit
/// (docs/design/17-coding-agent-harness.md, data). Flags are <see cref="CheckFlag"/> codes.
/// </summary>
public sealed record CheckRunRecord(
    Guid Id,
    string Commit,
    string? BaseCommit,
    int? BuildExitCode,
    int? TestsPassed,
    int? TestsFailed,
    int? TestsSkipped,
    int? TestCountBase,
    IReadOnlyList<string> Flags,
    DateTimeOffset RanAt);
