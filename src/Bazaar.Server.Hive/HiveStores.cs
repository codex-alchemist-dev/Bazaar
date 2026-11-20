using Bazaar.Server.Hive.Abstractions;

namespace Bazaar.Server.Hive;

/// <summary>Seam for the owner's grant matrix (doc 25, <c>hive_grants</c>). The default store is empty: nothing transfers.</summary>
public interface IHiveGrantStore
{
    /// <summary>True when a grant covers the project pair and kind.</summary>
    Task<bool> IsGrantedAsync(string fromProject, string toProject, HiveKind kind, CancellationToken ct);

    /// <summary>All grants.</summary>
    Task<IReadOnlyList<HiveGrant>> ListAsync(CancellationToken ct);

    /// <summary>Adds a grant; adding an existing one changes nothing.</summary>
    Task AddAsync(HiveGrant grant, CancellationToken ct);

    /// <summary>Removes a grant. Returns false when none matched.</summary>
    Task<bool> RemoveAsync(string fromProject, string toProject, HiveKind kind, CancellationToken ct);
}

/// <summary>A sanitized copy waiting for the review window or an owner approval.</summary>
/// <param name="Id">Pending id.</param>
/// <param name="Lesson">The lesson being copied.</param>
/// <param name="TargetProject">The project the grant covers.</param>
/// <param name="Slug">File slug, before any collision suffix.</param>
/// <param name="Title">Title of the copy.</param>
/// <param name="Body">Sanitized body.</param>
/// <param name="OriginTier">Tier of the source note, as written to the copy's <c>trust</c> field.</param>
/// <param name="SubmittedAt">When the lesson was submitted.</param>
/// <param name="ReleaseAt">When the review window ends; null when only an owner approval releases it.</param>
public sealed record HivePendingCopy(
    Guid Id,
    LessonRef Lesson,
    string TargetProject,
    string Slug,
    string Title,
    string Body,
    string OriginTier,
    DateTimeOffset SubmittedAt,
    DateTimeOffset? ReleaseAt);

/// <summary>A copy that was written (or reused) for one target project.</summary>
/// <param name="Path">Vault path of the copy.</param>
/// <param name="Lesson">The source lesson.</param>
/// <param name="TargetProject">The target project the grant covered.</param>
/// <param name="Revoked">True once the grant was revoked.</param>
public sealed record HiveCopyRecord(string Path, LessonRef Lesson, string TargetProject, bool Revoked);

/// <summary>Seam for held copies and the record of written ones.</summary>
public interface IHiveCopyLedger
{
    /// <summary>Holds a copy.</summary>
    Task AddPendingAsync(HivePendingCopy pending, CancellationToken ct);

    /// <summary>All held copies.</summary>
    Task<IReadOnlyList<HivePendingCopy>> ListPendingAsync(CancellationToken ct);

    /// <summary>Drops a held copy. Returns false when it is not held.</summary>
    Task<bool> RemovePendingAsync(Guid id, CancellationToken ct);

    /// <summary>Records a written copy.</summary>
    Task RecordCopyAsync(HiveCopyRecord record, CancellationToken ct);

    /// <summary>All copy records.</summary>
    Task<IReadOnlyList<HiveCopyRecord>> ListCopiesAsync(CancellationToken ct);

    /// <summary>Marks the records of a grant revoked and returns them as they were before.</summary>
    Task<IReadOnlyList<HiveCopyRecord>> MarkRevokedAsync(string fromProject, string toProject, HiveKind kind, CancellationToken ct);
}

/// <summary>Reads a Vault note by path. Used to deprecate existing copies in place.</summary>
public interface IHiveNoteReader
{
    /// <summary>The note text, or null when the path does not exist.</summary>
    Task<string?> ReadAsync(string path, CancellationToken ct);
}

/// <summary>In-memory grant store for tests and dev runs.</summary>
public sealed class InMemoryHiveGrantStore : IHiveGrantStore
{
    private readonly Lock _gate = new();
    private readonly List<HiveGrant> _grants = [];

    /// <inheritdoc />
    public Task<bool> IsGrantedAsync(string fromProject, string toProject, HiveKind kind, CancellationToken ct)
    {
        lock (_gate)
        {
            return Task.FromResult(_grants.Exists(g => Matches(g, fromProject, toProject, kind)));
        }
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<HiveGrant>> ListAsync(CancellationToken ct)
    {
        lock (_gate)
        {
            return Task.FromResult<IReadOnlyList<HiveGrant>>(_grants.ToList());
        }
    }

    /// <inheritdoc />
    public Task AddAsync(HiveGrant grant, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(grant);
        lock (_gate)
        {
            if (!_grants.Exists(g => Matches(g, grant.FromProject, grant.ToProject, grant.Kind)))
            {
                _grants.Add(grant);
            }
        }

        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task<bool> RemoveAsync(string fromProject, string toProject, HiveKind kind, CancellationToken ct)
    {
        lock (_gate)
        {
            return Task.FromResult(_grants.RemoveAll(g => Matches(g, fromProject, toProject, kind)) > 0);
        }
    }

    private static bool Matches(HiveGrant g, string from, string to, HiveKind kind) =>
        g.Kind == kind
        && string.Equals(g.FromProject, from, StringComparison.Ordinal)
        && string.Equals(g.ToProject, to, StringComparison.Ordinal);
}

/// <summary>In-memory ledger for tests and dev runs.</summary>
public sealed class InMemoryHiveCopyLedger : IHiveCopyLedger
{
    private readonly Lock _gate = new();
    private readonly List<HivePendingCopy> _pending = [];
    private readonly List<HiveCopyRecord> _copies = [];

    /// <inheritdoc />
    public Task AddPendingAsync(HivePendingCopy pending, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(pending);
        lock (_gate)
        {
            _pending.Add(pending);
        }

        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<HivePendingCopy>> ListPendingAsync(CancellationToken ct)
    {
        lock (_gate)
        {
            return Task.FromResult<IReadOnlyList<HivePendingCopy>>(_pending.ToList());
        }
    }

    /// <inheritdoc />
    public Task<bool> RemovePendingAsync(Guid id, CancellationToken ct)
    {
        lock (_gate)
        {
            return Task.FromResult(_pending.RemoveAll(p => p.Id == id) > 0);
        }
    }

    /// <inheritdoc />
    public Task RecordCopyAsync(HiveCopyRecord record, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(record);
        lock (_gate)
        {
            _copies.RemoveAll(c => SameTarget(c, record));
            _copies.Add(record);
        }

        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<HiveCopyRecord>> ListCopiesAsync(CancellationToken ct)
    {
        lock (_gate)
        {
            return Task.FromResult<IReadOnlyList<HiveCopyRecord>>(_copies.ToList());
        }
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<HiveCopyRecord>> MarkRevokedAsync(string fromProject, string toProject, HiveKind kind, CancellationToken ct)
    {
        lock (_gate)
        {
            var affected = new List<HiveCopyRecord>();
            for (var i = 0; i < _copies.Count; i++)
            {
                var c = _copies[i];
                if (!c.Revoked
                    && c.Lesson.Kind == kind
                    && string.Equals(c.Lesson.OriginProject, fromProject, StringComparison.Ordinal)
                    && string.Equals(c.TargetProject, toProject, StringComparison.Ordinal))
                {
                    affected.Add(c);
                    _copies[i] = c with { Revoked = true };
                }
            }

            return Task.FromResult<IReadOnlyList<HiveCopyRecord>>(affected);
        }
    }

    private static bool SameTarget(HiveCopyRecord a, HiveCopyRecord b) =>
        string.Equals(a.Path, b.Path, StringComparison.Ordinal)
        && string.Equals(a.TargetProject, b.TargetProject, StringComparison.Ordinal)
        && string.Equals(a.Lesson.OriginProject, b.Lesson.OriginProject, StringComparison.Ordinal)
        && string.Equals(a.Lesson.OriginNote, b.Lesson.OriginNote, StringComparison.Ordinal);
}
