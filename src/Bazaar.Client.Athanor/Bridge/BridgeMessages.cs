using System.Text.Json.Serialization;

namespace Bazaar.Client.Athanor.Bridge;

/// <summary>
/// A message the host sends to the editor over the bridge (doc 52 §4). The <c>type</c> discriminator is written by the
/// codec; <see cref="Seq"/> rises for every message the host sends.
/// </summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(OpenMessage), "open")]
[JsonDerivedType(typeof(CloseMessage), "close")]
[JsonDerivedType(typeof(SetThemeMessage), "setTheme")]
[JsonDerivedType(typeof(SetReadOnlyMessage), "setReadOnly")]
[JsonDerivedType(typeof(ShowDiffMessage), "showDiff")]
[JsonDerivedType(typeof(ApplyEditMessage), "applyEdit")]
[JsonDerivedType(typeof(FocusMessage), "focus")]
public abstract record HostMessage(long Seq);

/// <summary>Opens a document in the editor. <paramref name="Revision"/> is the server revision the content came from.</summary>
public sealed record OpenMessage(
    long Seq,
    string DocumentId,
    string Content,
    string Revision,
    string LanguageId,
    bool ReadOnly) : HostMessage(Seq);

/// <summary>Closes a document's model in the editor.</summary>
public sealed record CloseMessage(long Seq, string DocumentId) : HostMessage(Seq);

/// <summary>Applies a Monaco defineTheme payload (JSON text) without reloading the editor.</summary>
public sealed record SetThemeMessage(long Seq, string ThemeJson) : HostMessage(Seq);

/// <summary>Switches one document between editable and read-only.</summary>
public sealed record SetReadOnlyMessage(long Seq, string DocumentId, bool ReadOnly) : HostMessage(Seq);

/// <summary>Shows a side-by-side diff in the single diff component (doc 41 §4.15).</summary>
public sealed record ShowDiffMessage(long Seq, string Original, string Modified, string LanguageId) : HostMessage(Seq);

/// <summary>Replaces a line and column range of a document. Lines and columns are 1-based, as Monaco counts them.</summary>
public sealed record ApplyEditMessage(
    long Seq,
    string DocumentId,
    int StartLine,
    int StartColumn,
    int EndLine,
    int EndColumn,
    string Text) : HostMessage(Seq);

/// <summary>Moves keyboard focus into the editor.</summary>
public sealed record FocusMessage(long Seq) : HostMessage(Seq);

/// <summary>
/// A message the editor sends to the host (doc 52 §4). Decoded from untrusted input, so every field is required and
/// the codec checks the values after decoding.
/// </summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(ReadyMessage), "ready")]
[JsonDerivedType(typeof(ChangedMessage), "changed")]
[JsonDerivedType(typeof(SaveRequestedMessage), "saveRequested")]
[JsonDerivedType(typeof(KeyPressedMessage), "keyPressed")]
[JsonDerivedType(typeof(SelectionMessage), "selection")]
[JsonDerivedType(typeof(ErrorMessage), "error")]
public abstract record EditorMessage([property: JsonRequired] long Seq);

/// <summary>The editor finished loading and can take host messages.</summary>
public sealed record ReadyMessage([property: JsonRequired] long Seq) : EditorMessage(Seq);

/// <summary>The content changed. <paramref name="Version"/> is a per-document counter that only rises.</summary>
public sealed record ChangedMessage(
    [property: JsonRequired] long Seq,
    [property: JsonRequired] string DocumentId,
    [property: JsonRequired] long Version,
    [property: JsonRequired] string Content,
    [property: JsonRequired] bool Dirty) : EditorMessage(Seq);

/// <summary>The user asked to save a document (the save coordinator decides what happens, doc 52 §5).</summary>
public sealed record SaveRequestedMessage([property: JsonRequired] long Seq, [property: JsonRequired] string DocumentId)
    : EditorMessage(Seq);

/// <summary>A key the editor saw, handed to the client key router as the editor reports it (doc 52 §7).</summary>
public sealed record KeyPressedMessage(
    [property: JsonRequired] long Seq,
    [property: JsonRequired] string Key,
    [property: JsonRequired] bool Shift,
    [property: JsonRequired] bool Alt,
    [property: JsonRequired] bool Meta,
    [property: JsonRequired] bool Ctrl) : EditorMessage(Seq);

/// <summary>The selection moved. Lines and columns are 1-based.</summary>
public sealed record SelectionMessage(
    [property: JsonRequired] long Seq,
    [property: JsonRequired] string DocumentId,
    [property: JsonRequired] int StartLine,
    [property: JsonRequired] int StartColumn,
    [property: JsonRequired] int EndLine,
    [property: JsonRequired] int EndColumn) : EditorMessage(Seq);

/// <summary>The editor hit an error. <paramref name="DocumentId"/> is set when the error belongs to one document.</summary>
public sealed record ErrorMessage(
    [property: JsonRequired] long Seq,
    string? DocumentId,
    [property: JsonRequired] string Code,
    [property: JsonRequired] string Message) : EditorMessage(Seq);
