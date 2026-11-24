namespace Bazaar.Client.Athanor;

/// <summary>
/// One row of the language table: the language id and the extensions, well-known file names and interpreter names
/// that select it. Extensions and names match case-insensitively; interpreter names are bare (no path, no version).
/// </summary>
public sealed record LanguageDefinition(
    string LanguageId,
    IReadOnlyList<string> Extensions,
    IReadOnlyList<string> FileNames,
    IReadOnlyList<string> Interpreters);
