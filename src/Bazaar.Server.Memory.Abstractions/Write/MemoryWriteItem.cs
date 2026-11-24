namespace Bazaar.Server.Memory.Write;

/// <summary>One validated memory file write waiting to be committed.</summary>
/// <param name="WriteId">Stable id. The spool and the commit replay dedupe on it.</param>
/// <param name="Path">Vault-relative note path.</param>
/// <param name="Content">Full file content to write.</param>
/// <param name="GcId">GC the write came from, or null when none applies.</param>
/// <param name="TaskId">Task the write came from, or null when none applies.</param>
/// <param name="ValidationSummary">Short validation result for the commit trailer.</param>
public sealed record MemoryWriteItem(
    Guid WriteId,
    string Path,
    string Content,
    string? GcId,
    string? TaskId,
    string ValidationSummary);
