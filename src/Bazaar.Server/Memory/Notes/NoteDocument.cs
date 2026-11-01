namespace Bazaar.Server.Memory.Notes;

/// <summary>
/// A memory note as text: typed front matter, extra top-level fields and the Markdown body.
/// Serializing an unchanged document returns the exact input bytes. A changed front matter is written
/// in canonical form; the body is kept byte for byte unless it changed too. Immutable: every change
/// returns a new document.
/// </summary>
public sealed class NoteDocument
{
    private readonly string? _original;
    private readonly string _baselineFront;
    private readonly string _baselineBody;

    private NoteDocument(
        MemoryNote note,
        IReadOnlyList<KeyValuePair<string, FrontValue>> extraFields,
        string body,
        string? original,
        string baselineFront,
        string baselineBody)
    {
        Note = note;
        ExtraFields = extraFields;
        Body = body;
        _original = original;
        _baselineFront = baselineFront;
        _baselineBody = baselineBody;
    }

    /// <summary>The typed front matter.</summary>
    public MemoryNote Note { get; }

    /// <summary>Top-level front matter keys the schema does not define, kept in source order.</summary>
    public IReadOnlyList<KeyValuePair<string, FrontValue>> ExtraFields { get; }

    /// <summary>Everything after the closing <c>---</c> line, byte for byte.</summary>
    public string Body { get; }

    /// <summary>Parses a note. Malformed front matter yields a failed <see cref="NoteParseResult"/>, never an exception.</summary>
    public static NoteParseResult Parse(string text)
    {
        ArgumentNullException.ThrowIfNull(text);

        var pos = 0;
        if (FrontMatterCodec.NextLine(text, ref pos) != "---")
        {
            return NoteParseResult.Failure(NoteParseError.MissingOpeningDelimiter, "The note does not start with ---.");
        }

        var frontStart = pos;
        var frontEnd = -1;
        while (pos < text.Length)
        {
            var lineStart = pos;
            if (FrontMatterCodec.NextLine(text, ref pos) == "---")
            {
                frontEnd = lineStart;
                break;
            }
        }

        if (frontEnd < 0)
        {
            return NoteParseResult.Failure(NoteParseError.MissingClosingDelimiter, "The front matter has no closing ---.");
        }

        var front = text[frontStart..frontEnd];
        var body = text[pos..];

        if (!FrontMatterCodec.TryParseLines(front, out var entries, out var lineError, out var lineDetail))
        {
            return NoteParseResult.Failure(lineError, lineDetail);
        }

        if (!MemoryNoteFields.TryRead(entries, out var note, out var extras, out var fieldError, out var fieldDetail))
        {
            return NoteParseResult.Failure(fieldError, fieldDetail);
        }

        var parsed = note!;
        var baseline = MemoryNoteFields.WriteFront(parsed, extras);
        return NoteParseResult.Success(new NoteDocument(parsed, extras, body, text, baseline, body));
    }

    /// <summary>A new document with no original text; it serializes in canonical form.</summary>
    public static NoteDocument Create(MemoryNote note, string body)
    {
        ArgumentNullException.ThrowIfNull(note);
        ArgumentNullException.ThrowIfNull(body);
        return new NoteDocument(note, [], body, null, string.Empty, string.Empty);
    }

    /// <summary>A copy with new front matter. The body and original bytes are kept.</summary>
    public NoteDocument WithNote(MemoryNote note)
    {
        ArgumentNullException.ThrowIfNull(note);
        return new NoteDocument(note, ExtraFields, Body, _original, _baselineFront, _baselineBody);
    }

    /// <summary>A copy with a new body. The front matter and original bytes are kept.</summary>
    public NoteDocument WithBody(string body)
    {
        ArgumentNullException.ThrowIfNull(body);
        return new NoteDocument(Note, ExtraFields, body, _original, _baselineFront, _baselineBody);
    }

    /// <summary>
    /// The note text. Returns the original input when front matter and body still match what was parsed;
    /// otherwise the canonical front matter followed by the body.
    /// </summary>
    public string Serialize()
    {
        var front = MemoryNoteFields.WriteFront(Note, ExtraFields);
        if (_original is not null && string.Equals(front, _baselineFront, StringComparison.Ordinal)
            && string.Equals(Body, _baselineBody, StringComparison.Ordinal))
        {
            return _original;
        }

        return "---\n" + front + "---\n" + Body;
    }
}
