namespace Bazaar.Server.Subagents;

/// <summary>Why a spawn was refused. Every refusal is fail closed: nothing was started.</summary>
public enum SubagentRejection
{
    /// <summary>The spec or parent context is malformed.</summary>
    InvalidSpec,

    /// <summary>The allowlist is not a subset of the parent's.</summary>
    ToolsNotSubset,

    /// <summary>The model is not in the parent's whitelist.</summary>
    ModelNotAllowed,

    /// <summary>The child would be deeper than the depth limit.</summary>
    DepthExceeded,

    /// <summary>A read-only or worktree-less child was granted edit, exec or push tools.</summary>
    WriteToolsForReadOnly,

    /// <summary>The allowlist names a merge or approval tool; children never hold that authority.</summary>
    AuthorityToolRequested,

    /// <summary>Another writer of this parent is running and disjoint writers are off.</summary>
    WriterBusy,

    /// <summary>The declared paths overlap another running writer, or are missing or unsafe.</summary>
    PathsOverlap,

    /// <summary>The allocator returned a worktree another writer already holds.</summary>
    WorktreeInUse,

    /// <summary>The worktree allocator failed.</summary>
    WorktreeUnavailable,

    /// <summary>The spawn record could not be written, so the spawn is denied.</summary>
    TraceUnavailable,

    /// <summary>The runtime refused (its depth, concurrency or tool limit, doc 06).</summary>
    RuntimeLimit,
}

/// <summary>Thrown when a spawn is refused.</summary>
public sealed class SubagentRejectedException : Exception
{
    /// <summary>Creates the exception.</summary>
    public SubagentRejectedException(SubagentRejection reason, string message, Exception? inner = null)
        : base(message, inner)
    {
        Reason = reason;
    }

    /// <summary>The refusal reason.</summary>
    public SubagentRejection Reason { get; }
}
