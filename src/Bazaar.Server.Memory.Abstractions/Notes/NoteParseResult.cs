namespace Bazaar.Server.Memory.Notes;

/// <summary>Why a note's front matter was rejected.</summary>
public enum NoteParseError
{
    /// <summary>The text does not start with a <c>---</c> line.</summary>
    MissingOpeningDelimiter,

    /// <summary>No closing <c>---</c> line.</summary>
    MissingClosingDelimiter,

    /// <summary>A front matter line is not <c>key: value</c>.</summary>
    MalformedLine,

    /// <summary>Indentation or block mappings, which the subset does not support.</summary>
    UnsupportedSyntax,

    /// <summary>A value does not parse in the flow subset.</summary>
    MalformedValue,

    /// <summary>The same key appears twice.</summary>
    DuplicateKey,

    /// <summary>A required field is absent.</summary>
    MissingField,

    /// <summary>A field has the wrong type or an unknown value.</summary>
    InvalidField,

    /// <summary>The memory block has a key the schema does not define.</summary>
    UnknownMemoryField,
}

/// <summary>
/// Result of parsing a note. Bad input is reported here, never thrown: <see cref="Document"/> is set only
/// on success, and <see cref="Error"/> and <see cref="Detail"/> say why otherwise.
/// </summary>
public sealed record NoteParseResult
{
    private NoteParseResult(NoteDocument? document, NoteParseError? error, string? detail)
    {
        Document = document;
        Error = error;
        Detail = detail;
    }

    /// <summary>The parsed note, or null on failure.</summary>
    public NoteDocument? Document { get; }

    /// <summary>The failure kind, or null on success.</summary>
    public NoteParseError? Error { get; }

    /// <summary>A short structural description of the failure. Never quotes note content.</summary>
    public string? Detail { get; }

    /// <summary>True when <see cref="Document"/> is set.</summary>
    public bool IsSuccess => Document is not null;

    /// <summary>A successful parse.</summary>
    public static NoteParseResult Success(NoteDocument document) => new(document, null, null);

    /// <summary>A rejected parse.</summary>
    public static NoteParseResult Failure(NoteParseError error, string detail) => new(null, error, detail);
}
