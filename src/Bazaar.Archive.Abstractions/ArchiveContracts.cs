namespace Bazaar.Archive;

/// <summary>What a cold-storage unit is (doc 25 "Archive cold storage").</summary>
public enum ArchiveUnitKind
{
    GroupChat,
    Project,
}

/// <summary>Names one unit in the archive. <see cref="Id"/> is a path-safe segment: lowercase letters, digits, hyphens and dots.</summary>
public sealed record ArchiveUnit(ArchiveUnitKind Kind, string Id);

/// <summary>One record of a unit. <see cref="Kind"/> is the source's own label, such as <c>gc_message</c>; <c>thinking</c> is refused.</summary>
public sealed record ArchiveRecord(string Id, string Kind, DateTimeOffset At, string Body);

/// <summary>One segment of a bundle with the hash that proves it.</summary>
public sealed record ArchiveSegment(string Name, string Sha256, long Length, int RecordCount);

/// <summary>The manifest of one bundle. <see cref="RootHash"/> is SHA-256 over the segment hashes, in order.</summary>
public sealed record ArchiveManifest(
    int FormatVersion,
    ArchiveUnit Unit,
    DateTimeOffset ArchivedAt,
    int RecordCount,
    IReadOnlyList<ArchiveSegment> Segments,
    string RootHash);

/// <summary>Reads a unit's records in order. The owning module implements it and applies the exclusion floors.</summary>
public interface IArchiveSource
{
    IAsyncEnumerable<ArchiveRecord> ReadAsync(ArchiveUnit unit, CancellationToken ct);

    /// <summary>Called after the bundle verified: the unit becomes read-only in its owner. Nothing is deleted.</summary>
    Task MarkArchivedAsync(ArchiveUnit unit, string rootHash, CancellationToken ct);
}

/// <summary>Where bundles live. Files are addressed by unit and a relative name (<c>manifest.json</c>, <c>segments/0001.jsonl</c>).</summary>
public interface IArchiveStore
{
    Task PutAsync(ArchiveUnit unit, string name, ReadOnlyMemory<byte> content, CancellationToken ct);

    /// <summary>Returns null when the file is absent.</summary>
    Task<byte[]?> GetAsync(ArchiveUnit unit, string name, CancellationToken ct);

    Task<IReadOnlyList<string>> ListAsync(ArchiveUnit unit, CancellationToken ct);

    Task<IReadOnlyList<ArchiveUnit>> ListUnitsAsync(CancellationToken ct);
}

/// <summary>The owning module's unarchive path. Restore never overwrites a live record.</summary>
public interface IArchiveRestoreTarget
{
    /// <summary>Returns the SHA-256 (hex) of the live record's canonical form, or null when it does not exist.</summary>
    Task<string?> GetLiveHashAsync(ArchiveUnit unit, string recordId, CancellationToken ct);

    Task WriteAsync(ArchiveUnit unit, ArchiveRecord record, CancellationToken ct);
}

/// <summary>A hit in archived text.</summary>
public sealed record ArchiveSearchHit(ArchiveUnit Unit, string RecordId, DateTimeOffset At, string Snippet);

/// <summary>Term search over archived records; the Constellation and Vault search read it. Rebuildable from the bundles.</summary>
public interface IArchiveSearchIndex
{
    Task IndexAsync(ArchiveUnit unit, IReadOnlyList<ArchiveRecord> records, CancellationToken ct);

    Task<IReadOnlyList<ArchiveSearchHit>> SearchAsync(string query, int limit, CancellationToken ct);
}

/// <summary>The outcome of a verify: empty <see cref="Problems"/> means the bundle is intact.</summary>
public sealed record ArchiveVerifyResult(bool Ok, IReadOnlyList<string> Problems, ArchiveManifest? Manifest);

/// <summary>The outcome of a restore.</summary>
public sealed record ArchiveRestoreResult(int Restored, int AlreadyPresent, IReadOnlyList<string> Conflicts);
