namespace Bazaar.Server.Hive.Abstractions;

/// <summary>Grant storage (<c>hive_grants</c>). The owner changes grants; the flow only reads them.</summary>
public interface IHiveGrantStore
{
    /// <summary>Adds a grant. False when the same (from, to, kind) grant already exists.</summary>
    Task<bool> AddAsync(HiveGrant grant, CancellationToken ct);

    /// <summary>Removes a grant. False when there was none.</summary>
    Task<bool> RemoveAsync(ProjectId source, ProjectId target, string kind, CancellationToken ct);

    Task<bool> ExistsAsync(ProjectId source, ProjectId target, string kind, CancellationToken ct);

    Task<IReadOnlyList<HiveGrant>> ListAsync(CancellationToken ct);
}

/// <summary>Copy bookkeeping: review holds, active copies and deprecations.</summary>
public interface IHiveCopyStore
{
    /// <summary>Inserts a copy. False when a copy for the same (from, memory, to) already exists in any state.</summary>
    Task<bool> TryInsertAsync(HiveCopy copy, CancellationToken ct);

    Task<HiveCopy?> FindAsync(ProjectId source, string originMemory, ProjectId target, CancellationToken ct);

    Task<HiveCopy?> GetAsync(Guid copyId, CancellationToken ct);

    /// <summary>In-review copies whose review window ended at or before <paramref name="now"/>. Copies held with no window are never due.</summary>
    Task<IReadOnlyList<HiveCopy>> ListDueAsync(DateTimeOffset now, CancellationToken ct);

    /// <summary>Copies of one grant that are not deprecated (in review or active).</summary>
    Task<IReadOnlyList<HiveCopy>> ListLiveAsync(ProjectId source, ProjectId target, string kind, CancellationToken ct);

    Task<IReadOnlyList<HiveCopy>> ListAsync(CancellationToken ct);

    /// <summary>InReview to Active. False when the copy is not in review.</summary>
    Task<bool> MarkActiveAsync(Guid copyId, DateTimeOffset at, CancellationToken ct);

    /// <summary>Any live state to Deprecated. False when it already is.</summary>
    Task<bool> MarkDeprecatedAsync(Guid copyId, DateTimeOffset at, CancellationToken ct);
}

/// <summary>
/// Text check before a copy is held (doc 25: the sanitizer rejects text naming a project, customer, person or secret pattern).
/// It only accepts or rejects; it never rewrites. The real sanitizer is owned by doc 08 and adapts to this port.
/// </summary>
public interface IHiveSanitizer
{
    Task<HiveSanitizeResult> CheckAsync(string title, string body, CancellationToken ct);
}

public sealed record HiveSanitizeResult(bool Accepted, string? Reason)
{
    public static HiveSanitizeResult Ok { get; } = new(true, null);

    public static HiveSanitizeResult Reject(string reason) => new(false, reason);
}

/// <summary>
/// The narrow write handle into the Vault (doc 18 <c>VaultWriter</c>). Hive never deletes: the only change to an existing file is a
/// front matter edit.
/// </summary>
public interface IHiveVaultWriter
{
    /// <summary>Writes (or rewrites with the same content) a Markdown file at a vault-relative path.</summary>
    Task WriteAsync(string path, string markdown, CancellationToken ct);

    /// <summary>Sets one front matter key on an existing file.</summary>
    Task SetFrontMatterAsync(string path, string key, string value, CancellationToken ct);
}

/// <summary>Narrow audit port. The host adapts it to the audit log (doc 15).</summary>
public interface IHiveAudit
{
    Task AppendAsync(HiveAuditEvent entry, CancellationToken ct);
}

/// <summary>Reads the Hive settings; the host adapts it to the Settings table.</summary>
public interface IHiveSettings
{
    Task<HiveOptions> GetAsync(CancellationToken ct);
}
