namespace Bazaar.Server.Security.Audit.Reads;

/// <summary>
/// Who is reading and why, for the reads inside one <see cref="ReadAuditScope"/> (doc 48 section 5.1). Every field is a label
/// or an id, never content. <see cref="TaintTags"/> carries the session's taint tags (doc 15) when the caller has them.
/// </summary>
public sealed record ReadAccessContext(string Principal, string Route, string? Session = null, string? Purpose = null, IReadOnlyCollection<string>? TaintTags = null)
{
    /// <summary>
    /// Used for a read that happens outside any scope. The row still exists with this principal and route, so no read goes
    /// unrecorded, and a missing scope shows up in the log as <c>unattributed</c>.
    /// </summary>
    public static ReadAccessContext Unattributed { get; } = new(DataReadPayload.UnattributedPrincipal, DataReadPayload.UnattributedRoute);
}

/// <summary>
/// One read of server data, as a repository or a query layer reports it. <see cref="ObjectType"/> is the table or object
/// class. <see cref="ObjectId"/> is set only for a personal object, which is recorded on its own row (doc 48 section 5.1);
/// a bulk read leaves it null and records a count. Query shape is a hash of the normalised SQL, so the SQL text itself is
/// never stored.
/// </summary>
public sealed record DataRead(string ObjectType, int ResultCount, string? QueryKind = null, string? QueryShapeHash = null, string? ObjectId = null)
{
    /// <summary>A read of <paramref name="rowCount"/> rows from one statement. The statement text is hashed, not stored.</summary>
    public static DataRead FromSql(string objectType, string sql, int rowCount)
    {
        ArgumentNullException.ThrowIfNull(sql);
        return new DataRead(objectType, rowCount, QueryShape.KindOf(sql), QueryShape.Hash(sql));
    }
}
