namespace Bazaar.Server.Constellation.Memory.ReadLog;

/// <summary>The operation names of the read log (docs/design/constellation-memory-graph.md, "Owner-read access log").</summary>
public static class MemoryReadOperations
{
    public const string Retrieve = "retrieve";
    public const string Get = "get";
    public const string ListRevisions = "list_revisions";
    public const string Export = "export";

    /// <summary>True when <paramref name="operation"/> is one of the four.</summary>
    public static bool IsKnown(string operation) =>
        operation is Retrieve or Get or ListRevisions or Export;
}

/// <summary>
/// One read to record: who read, through which route and operation, and which ids came back. Ids only: never titles, bodies, vault
/// paths or query text. The log stamps the time and the id.
/// </summary>
/// <param name="ReaderPrincipal">The reader: the owner or another principal id, for example <c>owner</c> or <c>member:ada</c>.</param>
/// <param name="ReaderSession">The session or connection id; null when there is none.</param>
/// <param name="Route">The tool or wire route the read came through.</param>
/// <param name="SubjectAgentId">The agent whose private area was read; null for a read that touched no agent's private area.</param>
/// <param name="Operation">One of <see cref="MemoryReadOperations"/>.</param>
/// <param name="NoteIds">Memory note ids that were returned.</param>
/// <param name="DocIds">Vault doc ids that were returned ("D" format).</param>
/// <param name="TaintTags">Taint tags of the result, for example <c>labelled_quoted_data</c>.</param>
/// <param name="Purpose">An optional purpose given by the caller.</param>
public sealed record MemoryReadEntry(
    string ReaderPrincipal,
    string? ReaderSession,
    string Route,
    string? SubjectAgentId,
    string Operation,
    IReadOnlyList<string> NoteIds,
    IReadOnlyList<string> DocIds,
    IReadOnlyList<string> TaintTags,
    string? Purpose = null);

/// <summary>
/// Caller details a retrieval hands to the read log. When a retrieval gets none, the retriever derives the reader from the principal
/// and uses the route <see cref="DefaultRoute"/>.
/// </summary>
public sealed record MemoryReadContext(
    string ReaderPrincipal,
    string? Session = null,
    string? Route = null,
    string? Purpose = null,
    IReadOnlyList<string>? TaintTags = null)
{
    /// <summary>The route recorded when the caller names none.</summary>
    public const string DefaultRoute = "memory.graph.retrieve";
}

/// <summary>One stored row of the read log, as the owner's admin views list it.</summary>
public sealed record MemoryReadLogRecord(
    string ReadId,
    DateTimeOffset At,
    string ReaderPrincipal,
    string? ReaderSession,
    string Route,
    string? SubjectAgentId,
    string Operation,
    IReadOnlyList<string> NoteIds,
    IReadOnlyList<string> DocIds,
    bool IdsTruncated,
    int ResultCount,
    IReadOnlyList<string> TaintTags,
    string? Purpose,
    long AuditSeq);

/// <summary>
/// A page request, newest first. <paramref name="After"/> is the cursor of the previous page; <paramref name="SubjectAgentId"/> filters
/// to reads of one agent's private area.
/// </summary>
public sealed record MemoryReadLogQuery(string? SubjectAgentId = null, MemoryReadLogCursor? After = null, int Limit = 50)
{
    /// <summary>The page size ceiling.</summary>
    public const int MaxLimit = 200;
}

/// <summary>Keyset cursor over (time, read id), both descending.</summary>
public sealed record MemoryReadLogCursor(DateTimeOffset At, string ReadId);

/// <summary>One page of the log. <see cref="Next"/> is null on the last page.</summary>
public sealed record MemoryReadLogPage(IReadOnlyList<MemoryReadLogRecord> Records, MemoryReadLogCursor? Next);

/// <summary>
/// Records reads in the append-only owner-read log. Optional: with none registered nothing is logged. When a log is registered, a
/// read by a principal other than the subject agent is refused unless <see cref="RecordAsync"/> completed.
/// </summary>
public interface IMemoryReadLog
{
    /// <summary>
    /// Appends the entries and one hash-chained audit entry each, all in one transaction. Completes when committed.
    /// </summary>
    /// <exception cref="MemoryReadLogException">The entries could not be recorded.</exception>
    Task RecordAsync(IReadOnlyList<MemoryReadEntry> entries, CancellationToken ct);
}

/// <summary>Reads the log for the owner's admin views.</summary>
public interface IMemoryReadLogReader
{
    /// <summary>Lists rows newest first, filtered by subject agent when given.</summary>
    Task<MemoryReadLogPage> ListAsync(MemoryReadLogQuery query, CancellationToken ct);
}

/// <summary>A read could not be logged, so it is refused (fail closed).</summary>
public sealed class MemoryReadLogException : Exception
{
    public MemoryReadLogException(string message, Exception? inner = null)
        : base(message, inner)
    {
    }
}
