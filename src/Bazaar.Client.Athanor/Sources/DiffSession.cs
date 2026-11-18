namespace Bazaar.Client.Athanor.Sources;

/// <summary>
/// Shows a side-by-side diff in an editor host (doc 52 §3). The language comes from the registry, using the path name first
/// and the first line of the modified text only when the name does not decide it. The host is the single diff component
/// (doc 41 §4.15). This helper never touches the network.
/// </summary>
public static class DiffSession
{
    /// <summary>Shows <paramref name="original"/> against <paramref name="modified"/> with the language chosen for <paramref name="pathHint"/>.</summary>
    public static Task ShowAsync(
        IEditorHost host,
        string original,
        string modified,
        string pathHint,
        LanguageRegistry languages,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(host);
        ArgumentNullException.ThrowIfNull(original);
        ArgumentNullException.ThrowIfNull(modified);
        ArgumentNullException.ThrowIfNull(pathHint);
        ArgumentNullException.ThrowIfNull(languages);

        string languageId = languages.Resolve(pathHint, FirstLine(modified));
        return host.ShowDiffAsync(original, modified, languageId, cancellationToken);
    }

    private static string FirstLine(string text)
    {
        int newline = text.IndexOf('\n');
        string line = newline < 0 ? text : text[..newline];
        return line.TrimEnd('\r');
    }
}
