using System.Collections.Frozen;

namespace Bazaar.Client.Athanor.AvaloniaEdit;

/// <summary>
/// Maps the registry's language ids (<see cref="LanguageIds"/>) to a file extension that AvaloniaEdit's highlighting manager
/// knows (doc 57 §2). Only languages AvaloniaEdit ships a definition for are listed; every other id is plain text.
/// </summary>
public static class AvaloniaEditLanguages
{
    private static readonly FrozenDictionary<string, string> _extensions = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        [LanguageIds.CSharp] = ".cs",
        [LanguageIds.JavaScript] = ".js",
        [LanguageIds.Json] = ".json",
        [LanguageIds.JsonC] = ".json",
        [LanguageIds.Markdown] = ".md",
        [LanguageIds.Xml] = ".xml",
        [LanguageIds.Html] = ".html",
        [LanguageIds.Css] = ".css",
        [LanguageIds.Sql] = ".sql",
        [LanguageIds.Python] = ".py",
        [LanguageIds.PowerShell] = ".ps1",
    }.ToFrozenDictionary(StringComparer.Ordinal);

    /// <summary>The extension used to look up the highlighting definition, or null when the language has none (plain text).</summary>
    public static string? ExtensionFor(string languageId)
    {
        ArgumentNullException.ThrowIfNull(languageId);
        return _extensions.TryGetValue(languageId, out string? extension) ? extension : null;
    }
}
