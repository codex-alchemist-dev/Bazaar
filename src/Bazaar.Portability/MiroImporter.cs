// Source docs (checked 2026-11-15):
//   Board export help article: https://help.miro.com/hc/en-us/articles/360017572754-Board-export
//     Direct fetch returned 403 from this environment, so the article could not be read here. The
//     export formats are taken from the search summary only: image, PDF and CSV. The CSV export
//     covers tables and does not keep board structure. Header names and column layout are NOT
//     verified against a real Miro export.
//   Board items overview: https://developers.miro.com/docs/board-items
// This importer reads CSV only. The header matching (Type, Title, Name, Text, Content) and the row
// layout are MODELLED, not vendor-documented. The fixture is modelled too. Verify against a real
// sample before relying on it. JSON is not read: no Miro JSON export is documented.
using System.Globalization;
using System.Text;

namespace Bazaar.Portability;

/// <summary>
/// Miro export as CSV (RFC 4180 quoting, a header row required). Each data row becomes one record. The
/// text comes from a Text or Content column, the title from a Title or Name column (or the first 80
/// characters of the text), and the kind from a Type column. Rows without text are skipped.
/// </summary>
public sealed class MiroImporter : IPortabilityImporter
{
    private const int _titleLimit = 80;
    private const string _marker = "miro";
    private static readonly string[] _textColumns = ["text", "content"];
    private static readonly string[] _titleColumns = ["title", "name"];
    private static readonly string[] _typeColumns = ["type"];

    public string Id => "bazaar.import.miro";
    public string Version => "1.0.0";

    public DetectResult Detect(ImportSource source)
    {
        if (!source.Name.EndsWith(".csv", StringComparison.OrdinalIgnoreCase))
            return new DetectResult(0, "not a CSV file");

        var rows = ReadRows(Encoding.UTF8.GetString(source.Content));
        if (rows.Count == 0) return new DetectResult(0, "empty CSV");

        var header = rows[0];
        if (IndexOf(header, _textColumns) < 0 && IndexOf(header, _titleColumns) < 0)
            return new DetectResult(0, "no text or title column");

        return source.Name.Contains(_marker, StringComparison.OrdinalIgnoreCase)
            ? new DetectResult(0.8, "miro file name and columns")
            : new DetectResult(0.5, "text or title column");
    }

    public IReadOnlyList<SourceRecord> Parse(ImportSource source)
    {
        var rows = ReadRows(Encoding.UTF8.GetString(source.Content));
        if (rows.Count < 2) return [];

        var header = rows[0];
        var textIndex = IndexOf(header, _textColumns);
        var titleIndex = IndexOf(header, _titleColumns);
        var typeIndex = IndexOf(header, _typeColumns);

        var records = new List<SourceRecord>();
        for (var row = 1; row < rows.Count; row++)
        {
            var cells = rows[row];
            var text = Cell(cells, textIndex);
            if (string.IsNullOrWhiteSpace(text)) continue;

            var kind = Cell(cells, typeIndex);
            if (string.IsNullOrWhiteSpace(kind)) kind = "item";
            var title = Cell(cells, titleIndex);
            if (string.IsNullOrWhiteSpace(title)) title = Truncate(text);

            var key = SourceKeys.For(source.Name + "#" + row.ToString(CultureInfo.InvariantCulture), text);
            records.Add(new SourceRecord(
                key,
                kind,
                title,
                text,
                new Dictionary<string, string>
                {
                    ["format"] = "miro",
                    ["source"] = source.Name,
                    ["type"] = kind,
                }));
        }
        return records;
    }

    /// <summary>Index of the first header cell matching one of the names, ignoring case and spaces; -1 if none.</summary>
    private static int IndexOf(List<string> header, string[] names)
    {
        for (var i = 0; i < header.Count; i++)
        {
            var cell = header[i].Trim().ToLowerInvariant();
            if (Array.IndexOf(names, cell) >= 0) return i;
        }
        return -1;
    }

    private static string Cell(List<string> cells, int index) =>
        index >= 0 && index < cells.Count ? cells[index].Trim() : string.Empty;

    /// <summary>
    /// Reads RFC 4180 CSV: quoted fields may hold commas, line breaks and doubled quotes. Accepts LF and CRLF.
    /// A stray quote inside an unquoted field is kept as-is.
    /// </summary>
    internal static List<List<string>> ReadRows(string text)
    {
        var rows = new List<List<string>>();
        var row = new List<string>();
        var field = new StringBuilder();
        var quoted = false;

        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (quoted)
            {
                if (c == '"')
                {
                    if (i + 1 < text.Length && text[i + 1] == '"')
                    {
                        field.Append('"');
                        i++;
                    }
                    else
                    {
                        quoted = false;
                    }
                }
                else
                {
                    field.Append(c);
                }
                continue;
            }

            switch (c)
            {
                case '"' when field.Length == 0:
                    quoted = true;
                    break;
                case ',':
                    row.Add(field.ToString());
                    field.Clear();
                    break;
                case '\r':
                    break;
                case '\n':
                    row.Add(field.ToString());
                    field.Clear();
                    if (!(row.Count == 1 && row[0].Length == 0)) rows.Add(row);
                    row = [];
                    break;
                default:
                    field.Append(c);
                    break;
            }
        }

        if (field.Length > 0 || row.Count > 0)
        {
            row.Add(field.ToString());
            if (!(row.Count == 1 && row[0].Length == 0)) rows.Add(row);
        }
        return rows;
    }

    private static string Truncate(string text) =>
        text.Length <= _titleLimit ? text : text[.._titleLimit];
}
