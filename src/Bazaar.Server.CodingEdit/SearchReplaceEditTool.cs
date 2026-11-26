namespace Bazaar.Server.CodingEdit;

/// <summary>
/// Search/replace edit (doc 17): replaces the one occurrence of an exact search string. Zero occurrences and more than one are refused.
/// Occurrences are counted with overlaps, so an ambiguous search is never resolved by position.
/// </summary>
/// <remarks>
/// A repeated edit is refused as <see cref="EditRejectReason.NoMatch"/> once the search text is gone, so it never applies twice.
/// </remarks>
public sealed class SearchReplaceEditTool(IWorktreeFiles files) : IEditTool
{
    /// <inheritdoc />
    public EditFormat Format => EditFormat.SearchReplace;

    /// <inheritdoc />
    public async Task<EditResult> ApplyAsync(EditRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request is not SearchReplaceEditRequest edit)
        {
            return EditResult.Refused(EditRejectReason.Malformed, request.Path, "the edit does not match the agent's edit format");
        }

        var jail = await PathJail.AuthorizeAsync(files, edit.Path, ct);
        if (!jail.Allowed)
        {
            return EditResult.Refused(jail.Reason!.Value, edit.Path, jail.Detail);
        }

        if (string.IsNullOrEmpty(edit.Search) || edit.Replace is null || !LineText.TryEncode(edit.Replace, out _))
        {
            return EditResult.Refused(EditRejectReason.Malformed, jail.Path, "the search text is empty or the replacement is missing");
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

        var first = text.IndexOf(edit.Search, StringComparison.Ordinal);
        if (first < 0)
        {
            return EditResult.Refused(EditRejectReason.NoMatch, jail.Path, "the search text does not occur");
        }

        if (text.IndexOf(edit.Search, first + 1, StringComparison.Ordinal) >= 0)
        {
            return EditResult.Refused(EditRejectReason.AmbiguousMatch, jail.Path, "the search text occurs more than once");
        }

        var after = string.Concat(text.AsSpan(0, first), edit.Replace, text.AsSpan(first + edit.Search.Length));
        return await CommittedWrite.CommitAsync(files, jail.Path, before, after, ct);
    }
}
