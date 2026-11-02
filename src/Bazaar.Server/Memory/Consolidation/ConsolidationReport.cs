namespace Bazaar.Server.Memory.Consolidation;

/// <summary>How a consolidation run ended.</summary>
public enum ConsolidationStatus
{
    /// <summary>The run finished. Operations may have been accepted and committed, or none were accepted.</summary>
    Completed,

    /// <summary>Another run held the agent's lock. No model call was made.</summary>
    SkippedLocked,

    /// <summary>The time or token cap stopped the run. Nothing was committed.</summary>
    CapExceeded,

    /// <summary>The model reply was not the required JSON, or asked for a tool. Nothing was committed.</summary>
    MalformedOutput,

    /// <summary>The gateway reported a failure or the stream ended early. Nothing was committed.</summary>
    GatewayFailed,

    /// <summary>The batch commit failed. The episodes stay unconsumed for a later run.</summary>
    CommitFailed,
}

/// <summary>An operation the validator dropped. The rest of the run is unaffected.</summary>
/// <param name="Index">Position of the operation in the model's list.</param>
/// <param name="Op">The operation name as the model wrote it.</param>
/// <param name="Path">Target path the operation would have written, or null when none could be built.</param>
/// <param name="Reasons">SoulGuard reason names, or a short parse or field message.</param>
public sealed record RejectedOperation(int Index, string Op, string? Path, IReadOnlyList<string> Reasons);

/// <summary>
/// The outcome of one consolidation run. Equal inputs and a fixed model script give the same values.
/// </summary>
/// <param name="Status">How the run ended.</param>
/// <param name="EpisodeCount">Episodes sent to the model (quarantined episodes are excluded).</param>
/// <param name="QuarantinedExcluded">Quarantined episodes left out of the run.</param>
/// <param name="AcceptedCount">Operations that passed SoulGuard and went into the batch.</param>
/// <param name="Rejected">Operations dropped, with reasons.</param>
/// <param name="MaintenanceReport">Text for the agent's private GC.</param>
/// <param name="CommitSha">The commit sha when a batch was committed, otherwise null.</param>
/// <param name="CommitMessage">The message built by <c>MemoryCommitMessage.Build</c> for the batch, or null.</param>
/// <param name="TokensUsed">Tokens counted for the model call.</param>
/// <param name="TokensEstimated">True when the gateway reported no usage and the count is characters divided by four.</param>
/// <param name="Detail">A short reason for a non-completed status, or null.</param>
public sealed record ConsolidationReport(
    ConsolidationStatus Status,
    int EpisodeCount,
    int QuarantinedExcluded,
    int AcceptedCount,
    IReadOnlyList<RejectedOperation> Rejected,
    string MaintenanceReport,
    string? CommitSha,
    string? CommitMessage,
    long TokensUsed,
    bool TokensEstimated,
    string? Detail);
