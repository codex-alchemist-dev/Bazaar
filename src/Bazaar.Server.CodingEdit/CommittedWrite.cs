namespace Bazaar.Server.CodingEdit;

/// <summary>
/// The one place an edit touches the worktree. Nothing is written until the caller has validated the whole edit, and the write is
/// verified by reading it back. A failed verification restores the original bytes.
/// </summary>
internal static class CommittedWrite
{
    /// <summary>
    /// Writes <paramref name="afterText"/> over <paramref name="before"/> at <paramref name="path"/>. Identical content is a no-op
    /// with no write. A file that changed since it was read is a conflict, and nothing is written.
    /// </summary>
    public static async Task<EditResult> CommitAsync(
        IWorktreeFiles files,
        string path,
        WorktreeFile? before,
        string afterText,
        CancellationToken ct)
    {
        var after = LineText.EncodeValidated(afterText);
        var afterSha = ContentHash.Sha256Hex(after);
        var beforeSha = before?.Sha256;
        var beforeText = before is not null && LineText.TryDecode(before.Content, out var decoded) ? decoded : null;
        var changed = LineText.ChangedLines(beforeText, afterText);

        if (beforeSha == afterSha)
        {
            return EditResult.Succeeded(path, beforeSha, afterSha, 0);
        }

        // The file must still hold the bytes that were read. A concurrent change is refused, not overwritten.
        var current = await files.ReadAsync(path, ct);
        if (current?.Sha256 != beforeSha)
        {
            return EditResult.Refused(EditRejectReason.Conflict, path, "the file changed after it was read; nothing was written");
        }

        await files.WriteAsync(path, after, afterSha, ct);

        var verify = await files.ReadAsync(path, ct);
        if (verify?.Sha256 == afterSha)
        {
            return EditResult.Succeeded(path, beforeSha, afterSha, changed);
        }

        // The store did not keep the bytes. Put the original back, or remove the file if there was none.
        if (before is not null)
        {
            await files.WriteAsync(path, before.Content, before.Sha256, ct);
        }
        else
        {
            await files.DeleteAsync(path, ct);
        }

        return EditResult.Refused(EditRejectReason.WriteFailed, path, "the write did not verify; the original content was restored");
    }
}
