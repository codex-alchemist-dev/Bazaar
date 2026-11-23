using Bazaar.Contracts.Coding;

namespace Bazaar.Server.CodingStore;

/// <summary>
/// The write side of the append-only <c>tool_calls</c> table (docs/design/17-coding-agent-harness.md, data). The host
/// writes; a tool never writes its own row. A host adapter maps the CodingPermissions and Subagents trace ports onto this.
/// </summary>
public interface IToolCallLog
{
    /// <summary>Appends one row. A throw means the row was not written.</summary>
    ValueTask AppendAsync(ToolCallRecord record, CancellationToken cancellationToken);

    /// <summary>
    /// Records the outcome of a row that was appended with <see cref="ToolCallStatus.Unknown"/>. Returns false when the row does
    /// not exist or already has an outcome (the table allows this transition once).
    /// </summary>
    /// <param name="id">The row id.</param>
    /// <param name="status">The outcome; <see cref="ToolCallStatus.Unknown"/> is not an outcome and is rejected.</param>
    /// <param name="exitCode">The process exit code, when there was one.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    ValueTask<bool> CompleteAsync(Guid id, ToolCallStatus status, int? exitCode, CancellationToken cancellationToken);
}

/// <summary>Reads <c>tool_calls</c> rows so a run can be replayed.</summary>
public interface IToolCallReader
{
    /// <summary>Returns one row, or null when there is none.</summary>
    ValueTask<ToolCallRecord?> GetAsync(Guid id, CancellationToken cancellationToken);

    /// <summary>Returns a session's rows in start order.</summary>
    ValueTask<IReadOnlyList<ToolCallRecord>> ListBySessionAsync(string sessionId, CancellationToken cancellationToken);
}

/// <summary>Stores and reads <c>check_runs</c> rows (server-owned verification runs on a commit).</summary>
public interface ICheckRunStore
{
    /// <summary>Inserts one run. Rows are never changed afterwards.</summary>
    ValueTask AppendAsync(CheckRunRecord record, CancellationToken cancellationToken);

    /// <summary>Returns one run, or null when there is none.</summary>
    ValueTask<CheckRunRecord?> GetAsync(Guid id, CancellationToken cancellationToken);

    /// <summary>Returns the runs for a commit, newest first.</summary>
    ValueTask<IReadOnlyList<CheckRunRecord>> ListByCommitAsync(string commit, CancellationToken cancellationToken);
}
