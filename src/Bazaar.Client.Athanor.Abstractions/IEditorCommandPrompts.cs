namespace Bazaar.Client.Athanor;

/// <summary>A new document the user named in the prompt: the path as typed and the language they picked (doc 57 §8).</summary>
/// <param name="Name">The path as typed, optionally <c>workspace:path</c>. Not normalised; the handler validates it.</param>
/// <param name="LanguageId">The language id the user chose.</param>
public sealed record NewDocumentRequest(string Name, string LanguageId);

/// <summary>
/// Head-supplied prompts for the commands that need typed input (doc 57 §8). A null result means the user cancelled and
/// the command does nothing. Implementations return raw text; the command handler validates and normalises it.
/// </summary>
public interface IEditorCommandPrompts
{
    /// <summary>Asks which document to open. Returns the path as typed, optionally <c>workspace:path</c>, or null.</summary>
    Task<string?> AskOpenPathAsync(CancellationToken cancellationToken);

    /// <summary>Asks for the name and language of a new document, or null when cancelled.</summary>
    Task<NewDocumentRequest?> AskNewDocumentAsync(CancellationToken cancellationToken);
}
