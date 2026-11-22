namespace Bazaar.Server.Portability;

/// <summary>Result of an audit export: the bundle files and the self-check that passed.</summary>
public sealed record AuditExportResult(IReadOnlyList<BundleFile> Files, AuditReplayResult SelfCheck);

/// <summary>
/// Audit log exporter (doc 44 sections 4.6 and 6.1, work package 8). Streams rows in sequence order from a chosen
/// checkpoint, copies the checkpoints with their signatures untouched (it holds no key and never signs), runs the replay
/// self-check on the files it produced, and fails closed if anything does not verify. Encrypted fields stay ciphertext
/// because the stored payload text is copied as is. The export is read-only evidence: <c>audit/archive.json</c> marks it
/// as not part of the live chain, and an importer must archive it, never merge it (doc 44 section 8.2).
/// </summary>
public sealed class AuditExporter(IAuditLogReadSource source, IAuditSignatureVerifier verifier)
{
    /// <summary>The import rule written into the archive marker.</summary>
    public const string ImportRule = "Archive only (archive/audit/<job>). Never merged into the live chain (doc 44 section 8.2).";

    /// <summary>
    /// Exports the rows after the checkpoint whose last seq is <paramref name="startCheckpointLastSeq"/>, through the head
    /// (null: from the first row, with the genesis hash). The starting checkpoint is written first in the checkpoint file.
    /// Throws <see cref="AuditExportException"/> when the starting checkpoint is unknown or any check fails.
    /// </summary>
    public async Task<AuditExportResult> ExportAsync(long? startCheckpointLastSeq, CancellationToken ct = default)
    {
        var checkpoints = new List<AuditCheckpointRecord>();
        await foreach (var checkpoint in source.ReadCheckpointsAsync(startCheckpointLastSeq, ct).ConfigureAwait(false))
        {
            checkpoints.Add(checkpoint);
        }

        if (startCheckpointLastSeq is not null && (checkpoints.Count == 0 || checkpoints[0].LastSeq != startCheckpointLastSeq))
        {
            throw new AuditExportException($"The starting checkpoint with last seq {startCheckpointLastSeq} was not found.");
        }

        var fromSeq = startCheckpointLastSeq is null ? 1 : startCheckpointLastSeq.Value + 1;
        var rows = new List<AuditRow>();
        await foreach (var row in source.ReadRowsAsync(fromSeq, null, ct).ConfigureAwait(false))
        {
            rows.Add(row);
        }

        var entries = AuditArchiveFormat.WriteRows(rows);
        var checkpointFile = AuditArchiveFormat.WriteCheckpoints(checkpoints);

        // Self-check on the bytes that will ship, not on the in-memory objects, so a format bug cannot hide.
        var check = AuditArchiveVerifier.VerifyFiles(entries, checkpointFile, startsAtGenesis: startCheckpointLastSeq is null, verifier);
        if (!check.IsValid)
        {
            var failure = check.FirstMismatch!;
            throw new AuditExportException($"The audit export failed its self-check at seq {failure.Seq}: {failure.Kind} ({failure.Detail}).");
        }

        var marker = new ArchiveMarker(
            AuditArchiveFormat.ArchiveFormatId,
            PartOfLiveChain: false,
            ImportRule,
            startCheckpointLastSeq,
            FirstRowSeq: rows.Count == 0 ? 0 : rows[0].Seq,
            LastRowSeq: rows.Count == 0 ? 0 : rows[^1].Seq,
            RowCount: rows.Count,
            CheckpointCount: checkpoints.Count,
            UncheckpointedTailRows: check.UncheckpointedTailRows);

        var files = new List<BundleFile>
        {
            new(AuditArchiveFormat.EntriesPath, entries),
            new(AuditArchiveFormat.CheckpointsPath, checkpointFile),
            new(AuditArchiveFormat.ArchivePath, AuditArchiveFormat.WriteArchive(marker)),
        };
        return new AuditExportResult(files, check);
    }
}

/// <summary>Offline verification of an exported audit archive: the same replay the exporter runs as its self-check.</summary>
public static class AuditArchiveVerifier
{
    /// <summary>
    /// Replays the two files. Malformed files give a failed result, not an exception. Use
    /// <see cref="VerifyFiles(ReadOnlySpan{byte}, ReadOnlySpan{byte}, ReadOnlySpan{byte}, IAuditSignatureVerifier)"/> to take the start
    /// point from <c>audit/archive.json</c>.
    /// </summary>
    public static AuditReplayResult VerifyFiles(
        ReadOnlySpan<byte> entries, ReadOnlySpan<byte> checkpoints, bool startsAtGenesis, IAuditSignatureVerifier verifier)
    {
        ArgumentNullException.ThrowIfNull(verifier);

        var rows = AuditArchiveFormat.TryReadRows(entries);
        var points = AuditArchiveFormat.TryReadCheckpoints(checkpoints);
        if (rows is null || points is null)
        {
            return AuditReplayResult.Fail(0, 0, new AuditReplayFailure(0, AuditReplayFailureKind.MalformedInput, "An audit file is not valid."));
        }

        return AuditChainReplay.Replay(rows, points, verifier, startsAtGenesis);
    }

    /// <summary>
    /// Replays the archive and takes the start point from the marker. A marker that is missing, has an unknown format, or
    /// claims to be part of the live chain fails the check.
    /// </summary>
    public static AuditReplayResult VerifyFiles(
        ReadOnlySpan<byte> entries, ReadOnlySpan<byte> checkpoints, ReadOnlySpan<byte> archive, IAuditSignatureVerifier verifier)
    {
        var marker = AuditArchiveFormat.TryReadArchive(archive);
        if (marker is null || marker.Format != AuditArchiveFormat.ArchiveFormatId || marker.PartOfLiveChain)
        {
            return AuditReplayResult.Fail(0, 0, new AuditReplayFailure(0, AuditReplayFailureKind.MalformedInput, "The archive marker is missing or invalid."));
        }

        return VerifyFiles(entries, checkpoints, marker.StartCheckpointLastSeq is null, verifier);
    }
}
