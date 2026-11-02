namespace Bazaar.Server.Sanitization;

/// <summary>Removes sensitive spans from text that is about to leave a private scope.</summary>
public interface ITextSanitizer
{
    /// <summary>Returns the text with secrets, names, paths and quoted passages replaced by placeholders.</summary>
    /// <param name="text">The text to sanitize.</param>
    /// <param name="terms">Caller-supplied names and passages to remove.</param>
    SanitizedText Sanitize(string text, SensitiveTerms terms);
}
