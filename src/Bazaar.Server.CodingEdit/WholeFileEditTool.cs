namespace Bazaar.Server.CodingEdit;

/// <summary>
/// Whole-file edit (doc 17): the model sends the complete new content. A missing file is created. The content is validated before
/// anything is written, and identical content is a no-op.
/// </summary>
public sealed class WholeFileEditTool(IWorktreeFiles files) : IEditTool
{
    /// <inheritdoc />
    public EditFormat Format => EditFormat.WholeFile;

    /// <inheritdoc />
    public async Task<EditResult> ApplyAsync(EditRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request is not WholeFileEditRequest edit)
        {
            return EditResult.Refused(EditRejectReason.Malformed, request.Path, "the edit does not match the agent's edit format");
        }

        var jail = await PathJail.AuthorizeAsync(files, edit.Path, ct);
        if (!jail.Allowed)
        {
            return EditResult.Refused(jail.Reason!.Value, edit.Path, jail.Detail);
        }

        if (edit.Content is null || !LineText.TryEncode(edit.Content, out _))
        {
            return EditResult.Refused(EditRejectReason.Malformed, jail.Path, "the content is missing or is not valid text");
        }

        var before = await files.ReadAsync(jail.Path, ct);
        if (before is not null && !LineText.TryDecode(before.Content, out _))
        {
            // A whole-file write replaces the bytes. A binary file is replaced only through a format that says so.
            return EditResult.Refused(EditRejectReason.NotText, jail.Path, "the file is not valid UTF-8");
        }

        return await CommittedWrite.CommitAsync(files, jail.Path, before, edit.Content, ct);
    }
}
