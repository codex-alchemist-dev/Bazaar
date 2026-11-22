using Bazaar.Server.Portability;

namespace Bazaar.Server.Portability.Postgres.Tests;

/// <summary>Builders for store records. Times are whole microseconds, so a database round trip returns the same value.</summary>
public static class ImportStoreFixtures
{
    /// <summary>The job's creation time. Later times are offsets from it.</summary>
    public static readonly DateTimeOffset T0 = new(2026, 11, 18, 9, 0, 0, TimeSpan.Zero);

    /// <summary>A valid 64-character lowercase hex digest built from one repeated character.</summary>
    public static string Sha(char c) => new(c, 64);

    /// <summary>A new job with one manifest file and an upload limit of 1 MB.</summary>
    public static NewImportJob NewJob(string jobId, long maxUploadBytes = 1_000_000) => new(
        jobId,
        "owner",
        "chat-dump",
        "1.0.0",
        maxUploadBytes,
        [new ImportManifestFile("chat.txt", 1234, Sha('1'))],
        T0);

    /// <summary>An item for a job, with the default importer, version and content hash.</summary>
    public static ImportItemRecord Item(
        string jobId,
        string sourceKey,
        ImportTargetKind kind = ImportTargetKind.GcMessage,
        string version = "v1",
        string contentHash = "",
        string importerId = "chat-dump") => new(
        importerId,
        sourceKey,
        string.IsNullOrEmpty(contentHash) ? Sha('d') : contentHash,
        kind,
        "target-" + sourceKey,
        version,
        jobId);

    /// <summary>A batch whose counts and key bounds come from its items, the way the commit path builds them.</summary>
    public static ImportBatchRecord Batch(string jobId, int number, DateTimeOffset committedAt, params ImportItemRecord[] items)
    {
        var counts = items.GroupBy(i => i.TargetKind).ToDictionary(g => g.Key, g => g.Count());
        return new ImportBatchRecord(
            jobId,
            number,
            counts,
            items[0].SourceKey,
            items[^1].SourceKey,
            Sha('c'),
            committedAt);
    }
}
