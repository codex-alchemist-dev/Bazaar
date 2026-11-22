namespace Bazaar.Server.Subagents;

/// <summary>What a trace row records.</summary>
public enum SubagentTraceKind
{
    /// <summary>Written before the runner starts.</summary>
    Spawned,

    /// <summary>Written when the run ended.</summary>
    Finished,
}

/// <summary>One append-only trace row, written by the host, never by the child.</summary>
/// <param name="Kind">Spawned or finished.</param>
/// <param name="SubagentId">The subagent id.</param>
/// <param name="ParentId">The parent session.</param>
/// <param name="ChainId">The parent chain.</param>
/// <param name="Depth">The child's depth.</param>
/// <param name="ModelId">The model.</param>
/// <param name="Tools">The final tool allowlist, sorted.</param>
/// <param name="Worktree">The worktree mode.</param>
/// <param name="DeclaredPaths">Declared writer paths.</param>
/// <param name="At">When the row was written.</param>
/// <param name="Status">The run status, for finished rows.</param>
/// <param name="Usage">Usage, for finished rows.</param>
public sealed record SubagentTraceEntry(
    SubagentTraceKind Kind,
    string SubagentId,
    string ParentId,
    string ChainId,
    int Depth,
    string ModelId,
    IReadOnlyList<string> Tools,
    WorktreeRequest Worktree,
    IReadOnlyList<string> DeclaredPaths,
    DateTimeOffset At,
    SubagentStatus? Status = null,
    SubagentUsage? Usage = null);

/// <summary>Port: append-only spawn record, written by the host.</summary>
public interface ISubagentTrace
{
    /// <summary>Appends a row. A throw denies the spawn (fail closed).</summary>
    Task AppendAsync(SubagentTraceEntry entry, CancellationToken cancellationToken);
}
