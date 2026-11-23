namespace Bazaar.Server.CodingEdit;

/// <summary>
/// Line-anchored edit (doc 17): replaces a 1-based inclusive line range after the model proves the current lines with a SHA-256.
/// A range that already holds the replacement is a no-op, so re-sending an applied edit does nothing.
/// </summary>
public sealed class LineAnchoredEditTool(IWorktreeFiles files) : IEditTool
{
    /// <inheritdoc />
    public EditFormat Format => EditFormat.LineAnchored;

    /// <inheritdoc />
    public async Task<EditResult> ApplyAsync(EditRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request is not LineAnchoredEditRequest edit)
        {
            return EditResult.Refused(EditRejectReason.Malformed, request.Path, "the edit does not match the agent's edit format");
        }

        var jail = await PathJail.AuthorizeAsync(files, edit.Path, ct);
        if (!jail.Allowed)
        {
            return EditResult.Refused(jail.Reason!.Value, edit.Path, jail.Detail);
        }

        if (edit.Replacement is null || !LineText.TryEncode(edit.Replacement, out _))
        {
            return EditResult.Refused(EditRejectReason.Malformed, jail.Path, "the replacement is missing or is not valid text");
        }

        if (!ContentHash.IsSha256Hex(edit.ExpectedLinesSha256))
        {
            return EditResult.Refused(EditRejectReason.Malformed, jail.Path, "the expected hash is not a SHA-256 hex digest");
        }

        if (edit.StartLine < 1 || edit.EndLine < edit.StartLine)
        {
            return EditResult.Refused(EditRejectReason.Malformed, jail.Path, "the line range is invalid");
        }

        var before = await files.ReadAsync(jail.Path, ct);
        if (before is null)
        {
            return EditResult.Refused(EditRejectReason.NotFound, jail.Path, "the file does not exist");
        }

        if (!LineText.TryDecode(before.Content, out var text))
        {
            return EditResult.Refused(EditRejectReason.NotText, jail.Path, "the file is not valid UTF-8");
        }

        var lines = LineText.Lines(text, out var trailingNewline);
        if (edit.EndLine > lines.Count)
        {
            return EditResult.Refused(EditRejectReason.Malformed, jail.Path, "the line range is beyond the end of the file");
        }

        var start = edit.StartLine - 1;
        var count = edit.EndLine - start;
        var currentRange = string.Join('\n', lines.GetRange(start, count));
        var anchored = string.Equals(LineText.Sha256(currentRange), edit.ExpectedLinesSha256, StringComparison.OrdinalIgnoreCase);

        var edited = new List<string>(lines);
        edited.RemoveRange(start, count);
        edited.InsertRange(start, LineText.ReplacementLines(edit.Replacement));
        var after = LineText.Join(edited, trailingNewline);

        // A stale anchor is accepted only when the range already holds the replacement, which makes a repeated edit a no-op.
        if (!anchored && after != text)
        {
            return EditResult.Refused(EditRejectReason.AnchorMismatch, jail.Path, "the lines at that range changed since they were read");
        }

        return await CommittedWrite.CommitAsync(files, jail.Path, before, after, ct);
    }
}
