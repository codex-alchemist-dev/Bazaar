using System.Text.Json;
using Bazaar.Client.Abstractions.Panes;
using Bazaar.Client.Panes.Abstractions;
using Bazaar.Contracts.Panes;

namespace Bazaar.Client.Archive.Panes;

/// <summary>
/// The read-only Archive book (pane kind <see cref="PaneKinds.Archive"/>, doc 25). The server writes the chapters and sends
/// them as data; this view model only formats them and moves the selection. Model (doc 25, pane models):
/// <c>{ "chapters": [ { "id", "title", "version", "predecessorId", "sentences": [ { "text", "sourceRef" } ] } ] }</c>.
/// A null, blank or missing <c>sourceRef</c> renders as an explicit "missing source" link, never as a blank. Nothing here
/// edits the Archive: there are no edit commands. A missing required field raises <see cref="PaneModelException"/>.
/// </summary>
public sealed class ArchivePaneViewModel : IPaneViewModel
{
    /// <summary>The text a sentence shows in place of a source it does not have.</summary>
    public const string MissingSourceLabel = "missing source";

    private readonly ArchiveChapterRow[] _chapters;
    private int _selected;

    public ArchivePaneViewModel(PaneDescriptor descriptor)
    {
        ArgumentNullException.ThrowIfNull(descriptor);
        Kind = descriptor.Kind;
        Title = "Archive";

        _chapters = PaneValues.Array(descriptor.Model, "chapters").EnumerateArray().Select(ReadChapter).ToArray();
        _selected = _chapters.Length > 0 ? 0 : -1;
    }

    public string Kind { get; }

    public string Title { get; }

    /// <summary>The chapters in the order the server sent them.</summary>
    public IReadOnlyList<ArchiveChapterRow> Chapters => _chapters;

    /// <summary>The zero-based position of the selected chapter, or -1 when the book has no chapters.</summary>
    public int SelectedIndex => _selected;

    /// <summary>The chapter on show, or null when the book has no chapters.</summary>
    public ArchiveChapterRow? SelectedChapter => _selected >= 0 ? _chapters[_selected] : null;

    /// <summary>The chapter after the selected one, or null at the end of the book.</summary>
    public ArchiveChapterRow? NextChapter => _selected >= 0 && _selected + 1 < _chapters.Length ? _chapters[_selected + 1] : null;

    /// <summary>The chapter before the selected one, or null at the start of the book.</summary>
    public ArchiveChapterRow? PreviousChapter => _selected > 0 ? _chapters[_selected - 1] : null;

    /// <summary>The chapter the selected one regenerates, when that chapter is in this book; otherwise null.</summary>
    public ArchiveChapterRow? PredecessorChapter =>
        SelectedChapter?.PredecessorId is { } predecessor ? _chapters.FirstOrDefault(c => c.Id == predecessor) : null;

    /// <summary>Selects the chapter with <paramref name="id"/>. Returns false, and keeps the selection, when no chapter has that id.</summary>
    public bool SelectChapter(string id)
    {
        ArgumentNullException.ThrowIfNull(id);
        var index = Array.FindIndex(_chapters, c => c.Id == id);
        if (index < 0)
        {
            return false;
        }

        _selected = index;
        return true;
    }

    /// <summary>Moves to the next chapter. Returns false at the end of the book.</summary>
    public bool SelectNext() => NextChapter is { } next && SelectChapter(next.Id);

    /// <summary>Moves to the previous chapter. Returns false at the start of the book.</summary>
    public bool SelectPrevious() => PreviousChapter is { } previous && SelectChapter(previous.Id);

    private static ArchiveChapterRow ReadChapter(JsonElement chapter)
    {
        if (chapter.ValueKind != JsonValueKind.Object)
        {
            throw new PaneModelException("each chapter must be an object");
        }

        var sentences = PaneValues.Array(chapter, "sentences").EnumerateArray().Select(ReadSentence).ToArray();
        return new ArchiveChapterRow(
            PaneValues.RequiredString(chapter, "id"),
            PaneValues.RequiredString(chapter, "title"),
            PaneValues.Scalar(chapter, "version"),
            ReadPredecessor(chapter),
            sentences);
    }

    /// <summary>The predecessor id. Absent, null or blank means a first chapter; any other type is a shape mismatch.</summary>
    private static string? ReadPredecessor(JsonElement chapter)
    {
        if (!chapter.TryGetProperty("predecessorId", out var value) || value.ValueKind == JsonValueKind.Null)
        {
            return null;
        }

        if (value.ValueKind != JsonValueKind.String)
        {
            throw new PaneModelException("model field 'predecessorId' must be a string or null");
        }

        var id = value.GetString()!.Trim();
        return id.Length == 0 ? null : id;
    }

    private static ArchiveSentenceRow ReadSentence(JsonElement sentence)
    {
        if (sentence.ValueKind != JsonValueKind.Object)
        {
            throw new PaneModelException("each sentence must be an object");
        }

        return new ArchiveSentenceRow(PaneValues.RequiredString(sentence, "text"), ReadSourceRef(sentence));
    }

    /// <summary>The trimmed source ref, or null when the sentence has none (absent, null, blank or whitespace only).</summary>
    private static string? ReadSourceRef(JsonElement sentence)
    {
        if (!sentence.TryGetProperty("sourceRef", out var value) || value.ValueKind == JsonValueKind.Null)
        {
            return null;
        }

        if (value.ValueKind != JsonValueKind.String)
        {
            throw new PaneModelException("model field 'sourceRef' must be a string or null");
        }

        var reference = value.GetString()!.Trim();
        return reference.Length == 0 ? null : reference;
    }
}

/// <summary>One chapter row. <see cref="PredecessorId"/> is null for a first chapter.</summary>
public sealed record ArchiveChapterRow(
    string Id,
    string Title,
    string Version,
    string? PredecessorId,
    IReadOnlyList<ArchiveSentenceRow> Sentences);

/// <summary>One sentence and its source. <see cref="SourceRef"/> is null when the sentence has no usable source.</summary>
public sealed record ArchiveSentenceRow(string Text, string? SourceRef)
{
    /// <summary>True when the link to show is the explicit "missing source" link.</summary>
    public bool IsMissingSource => SourceRef is null;

    /// <summary>The link text: the source ref, or "missing source".</summary>
    public string SourceLabel => SourceRef ?? ArchivePaneViewModel.MissingSourceLabel;
}
