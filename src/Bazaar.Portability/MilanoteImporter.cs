// Source docs (checked 2026-11-15):
//   https://help.milanote.com/en/articles/111395-exporting-your-work
// The article lists Markdown among the board export formats, alongside PDF, PNG, Word and plain text,
// and a "Generate ZIP" action. It gives no layout for the Markdown file. The layout below (one card per
// heading, the card's text as the body) is MODELLED, not verified against a real Milanote export. The
// fixture is modelled too. Verify against a real sample before relying on it.
// ZIP packages are not read here: the article names the ZIP but does not describe its contents.
using System.Globalization;
using System.Text;

namespace Bazaar.Portability;

/// <summary>
/// Milanote board export as Markdown. Each heading line starts a card: its text becomes the title and the
/// lines below it, up to the next heading, become the body. Text before the first heading becomes one card
/// titled by its first line. Blank cards are skipped.
/// </summary>
public sealed class MilanoteImporter : IPortabilityImporter
{
    private const int _titleLimit = 80;
    private const string _marker = "milanote";

    public string Id => "bazaar.import.milanote";
    public string Version => "1.0.0";

    public DetectResult Detect(ImportSource source)
    {
        if (!IsMarkdownName(source.Name)) return new DetectResult(0, "not a Markdown file");

        var text = Encoding.UTF8.GetString(source.Content);
        if (source.Name.Contains(_marker, StringComparison.OrdinalIgnoreCase))
            return new DetectResult(0.8, "milanote file name");
        return HeadingCount(text) > 0
            ? new DetectResult(0.3, "Markdown headings only")
            : new DetectResult(0, "no headings");
    }

    public IReadOnlyList<SourceRecord> Parse(ImportSource source)
    {
        var text = Encoding.UTF8.GetString(source.Content);
        var cards = SplitCards(text);
        var records = new List<SourceRecord>();
        for (var index = 0; index < cards.Count; index++)
        {
            var (title, body) = cards[index];
            if (string.IsNullOrWhiteSpace(title) && string.IsNullOrWhiteSpace(body)) continue;

            var key = SourceKeys.For(source.Name + "#" + index.ToString(CultureInfo.InvariantCulture), body);
            records.Add(new SourceRecord(
                key,
                "note",
                string.IsNullOrWhiteSpace(title) ? Truncate(body.Trim()) : title,
                body.Trim(),
                new Dictionary<string, string>
                {
                    ["format"] = "milanote",
                    ["source"] = source.Name,
                    ["type"] = "note",
                }));
        }
        return records;
    }

    /// <summary>Splits the text into (title, body) pairs at each heading line.</summary>
    private static List<(string Title, string Body)> SplitCards(string text)
    {
        var cards = new List<(string Title, string Body)>();
        string? title = null;
        var body = new StringBuilder();

        foreach (var line in text.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n'))
        {
            if (TryHeading(line, out var heading))
            {
                if (title is not null || body.Length > 0) cards.Add((title ?? string.Empty, body.ToString()));
                title = heading;
                body.Clear();
                continue;
            }
            body.AppendLine(line);
        }

        if (title is not null || body.Length > 0) cards.Add((title ?? string.Empty, body.ToString()));
        return cards;
    }

    private static bool TryHeading(string line, out string heading)
    {
        heading = string.Empty;
        var trimmed = line.TrimStart();
        var hashes = 0;
        while (hashes < trimmed.Length && trimmed[hashes] == '#') hashes++;
        if (hashes is 0 or > 6) return false;
        if (hashes >= trimmed.Length || trimmed[hashes] != ' ') return false;

        heading = Truncate(trimmed[(hashes + 1)..].Trim());
        return true;
    }

    private static int HeadingCount(string text) =>
        text.Split('\n').Count(line => TryHeading(line, out _));

    private static bool IsMarkdownName(string name) =>
        name.EndsWith(".md", StringComparison.OrdinalIgnoreCase)
        || name.EndsWith(".markdown", StringComparison.OrdinalIgnoreCase);

    private static string Truncate(string text) =>
        text.Length <= _titleLimit ? text : text[.._titleLimit];
}
