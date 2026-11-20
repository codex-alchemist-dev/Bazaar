namespace Bazaar.Client.Athanor.Wire;

/// <summary>Error codes of the editor wire pair (doc 57 §6).</summary>
public static class EditorErrorCodes
{
    public const string NotFound = "editor.not_found";
    public const string Conflict = "editor.conflict";
    public const string Forbidden = "editor.forbidden";
    public const string TooLarge = "editor.too_large";
    public const string Binary = "editor.binary";
    public const string ReadOnlyWorkspace = "editor.read_only_workspace";
    public const string Unavailable = "editor.unavailable";

    /// <summary>Not a server code: the answer could not be understood (doc 57 §6, PROPOSED).</summary>
    public const string Malformed = "editor.malformed";
}

/// <summary>
/// An open that failed for a reason other than "no such file". <see cref="IEditorFileSource.OpenAsync"/> can only say
/// document or null, so the refusal is thrown with the doc 57 §6 code for the caller to show (doc 57 §6, PROPOSED).
/// </summary>
public sealed class EditorFileException(string code, string message, Exception? inner = null) : Exception(message, inner)
{
    /// <summary>One of <see cref="EditorErrorCodes"/>, or a wire code a newer server added.</summary>
    public string Code { get; } = code;
}
