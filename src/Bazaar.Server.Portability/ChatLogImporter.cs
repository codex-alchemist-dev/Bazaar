using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace Bazaar.Server.Portability;

/// <summary>
/// Generic chat-log text dumps: one message per record, detected by line structure and never by extension.
/// Only three generic shapes are recognised; no product-specific export format is assumed:
/// <list type="bullet">
/// <item><description><c>[timestamp] name: text</c></description></item>
/// <item><description><c>timestamp - name: text</c></description></item>
/// <item><description><c>timestamp name: text</c> (a bare date-time, without brackets or a dash)</description></item>
/// </list>
/// Lines that do not start with one of these shapes are continuation lines of the previous message. Timestamps
/// are normalised to ISO-8601 UTC when they hold a full date and time; a timestamp without an offset is read as
/// UTC. Anything else is kept as the raw text. Parsing never throws on malformed input.
/// </summary>
public sealed class ChatLogImporter : IPortabilityImporter
{
    // A timestamp token for the dash and bare shapes: an ISO-like date (optionally with time and offset), or a bare time.
    private const string _timestamp =
        @"\d{4}-\d{2}-\d{2}(?:[ T]\d{1,2}:\d{2}(?::\d{2}(?:\.\d+)?)?)?(?:Z|[+-]\d{2}:?\d{2})?|\d{1,2}:\d{2}(?::\d{2})?";
    private const string _name = @"(?<name>[^:\[\]\r\n]{1,40}?)";
    private const string _text = @"(?::(?:\s(?<text>.*))?)$";

    private static readonly Regex _bracketed = new(
        @"^\[(?<ts>[^\]\r\n]{1,64})\]\s*" + _name + _text, RegexOptions.CultureInvariant);
    private static readonly Regex _dashed = new(
        "^(?<ts>" + _timestamp + @")\s+-\s+" + _name + _text, RegexOptions.CultureInvariant);
    private static readonly Regex _bare = new(
        "^(?<ts>" + _timestamp + @")\s+" + _name + _text, RegexOptions.CultureInvariant);

    private static readonly string[] _formatsWithoutOffset =
    [
        "yyyy-MM-dd'T'HH:mm:ss.FFFFFFF", "yyyy-MM-dd'T'HH:mm:ss", "yyyy-MM-dd'T'HH:mm",
        "yyyy-MM-dd HH:mm:ss.FFFFFFF", "yyyy-MM-dd HH:mm:ss", "yyyy-MM-dd HH:mm",
    ];
    private static readonly string[] _formatsWithOffset =
    [
        "yyyy-MM-dd'T'HH:mm:ss.FFFFFFFK", "yyyy-MM-dd'T'HH:mm:ssK", "yyyy-MM-dd'T'HH:mmK",
        "yyyy-MM-dd HH:mm:ss.FFFFFFFK", "yyyy-MM-dd HH:mm:ssK", "yyyy-MM-dd HH:mmK",
    ];

    // Detection: at least this many message-start lines, and a fraction that sets the confidence band.
    private const int _minMatchingLines = 2;
    private const double _bandThreshold = 0.6;
    private const double _belowBandMax = 0.25;
    private const int _titleMaxLength = 60;

    public string Id => "bazaar.import.chatlog";
    public string Version => "1.0.0";

    public DetectResult Detect(ImportSource source)
    {
        if (source.Content.Length == 0) return new DetectResult(0, "empty");
        if (Array.IndexOf(source.Content, (byte)0) >= 0) return new DetectResult(0, "binary (NUL bytes)");

        var lines = Lines(source.Content);
        var nonEmpty = 0;
        var matching = 0;
        foreach (var line in lines)
        {
            if (line.Trim().Length == 0) continue;
            nonEmpty++;
            if (TryMatchStart(line, out _)) matching++;
        }

        if (matching < _minMatchingLines) return new DetectResult(0, "no chat-line structure");

        var fraction = (double)matching / nonEmpty;
        // Below 60% the confidence stays under the plain-text importer's 0.3; at 60% and above it rises with the fraction.
        var confidence = fraction < _bandThreshold
            ? _belowBandMax * fraction / _bandThreshold
            : 0.5 + 0.4 * fraction;
        var reason = $"chat-line structure on {matching} of {nonEmpty} lines";
        return new DetectResult(Math.Round(confidence, 3), reason);
    }

    public IReadOnlyList<SourceRecord> Parse(ImportSource source)
    {
        var lines = Lines(source.Content);
        var messages = new List<Message>();
        Message? current = null;

        for (var i = 0; i < lines.Count; i++)
        {
            var line = lines[i];
            if (TryMatchStart(line, out var start))
            {
                current = new Message(i + 1, start.Timestamp, start.Name, start.Text, [line]);
                messages.Add(current);
                continue;
            }

            // Continuation: any other line, including malformed ones. A leading line with no message becomes an
            // unattributed message so that no content is dropped.
            if (current is null)
            {
                current = new Message(i + 1, string.Empty, string.Empty, string.Empty, []);
                messages.Add(current);
            }
            current.Raw.Add(line);
            current.Body.Add(line);
        }

        var records = new List<SourceRecord>(messages.Count);
        for (var index = 0; index < messages.Count; index++)
        {
            var message = messages[index];
            var raw = string.Join('\n', message.Raw).TrimEnd();
            var body = string.Join('\n', message.Body).TrimEnd();
            var timestamp = NormaliseTimestamp(message.Timestamp);
            var attributes = new Dictionary<string, string>
            {
                ["format"] = "chat-log",
                ["source"] = source.Name,
                ["line"] = message.Line.ToString(CultureInfo.InvariantCulture),
                ["author"] = message.Author,
                ["timestamp"] = timestamp,
            };
            var title = TitleFor(message.Author, body);
            // The key covers the message's own text and its position, so a re-import of the same dump is a no-op.
            var key = SourceKeys.For(source.Name, index.ToString(CultureInfo.InvariantCulture) + "\n" + raw);
            records.Add(new SourceRecord(key, ImportCommitter.GcEntryKind, title, body, attributes));
        }
        return records;
    }

    private sealed class Message(int line, string timestamp, string author, string text, List<string> raw)
    {
        public int Line { get; } = line;
        public string Timestamp { get; } = timestamp;
        public string Author { get; } = author;
        public List<string> Body { get; } = text.Length > 0 ? [text] : [];
        public List<string> Raw { get; } = raw;
    }

    private readonly record struct Start(string Timestamp, string Name, string Text);

    /// <summary>Matches a message-start line in any of the three shapes, with a name that has a letter or digit.</summary>
    private static bool TryMatchStart(string line, out Start start)
    {
        foreach (var pattern in new[] { _bracketed, _dashed, _bare })
        {
            var match = pattern.Match(line);
            if (!match.Success) continue;
            var name = match.Groups["name"].Value.Trim();
            if (name.Length == 0 || !name.Any(char.IsLetterOrDigit)) continue;
            var text = match.Groups["text"].Success ? match.Groups["text"].Value : string.Empty;
            start = new Start(match.Groups["ts"].Value.Trim(), name, text);
            return true;
        }
        start = default;
        return false;
    }

    /// <summary>ISO-8601 UTC when the value holds a full date and time (offset optional, read as UTC); else the raw text.</summary>
    private static string NormaliseTimestamp(string raw)
    {
        if (raw.Length == 0) return string.Empty;
        const DateTimeStyles styles = DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal;
        // Date-only and time-only values are kept raw: a date with no time is not an instant, and a bare time would
        // take today's date, which would make re-imports non-deterministic.
        if (DateTimeOffset.TryParseExact(raw, _formatsWithOffset, CultureInfo.InvariantCulture, styles, out var withOffset)
            && HasTime(raw))
        {
            return withOffset.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture);
        }
        if (DateTimeOffset.TryParseExact(raw, _formatsWithoutOffset, CultureInfo.InvariantCulture, styles, out var plain)
            && HasTime(raw))
        {
            return plain.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture);
        }
        return raw;
    }

    private static bool HasTime(string raw) => raw.Length > 10 && (raw[10] == ' ' || raw[10] == 'T');

    private static string TitleFor(string author, string body)
    {
        var firstLine = body.Split('\n', 2)[0].Trim();
        if (firstLine.Length > _titleMaxLength) firstLine = firstLine[.._titleMaxLength];
        return author.Length == 0 ? firstLine : $"{author}: {firstLine}";
    }

    /// <summary>Splits on LF after normalising CRLF, dropping the trailing empty element a final newline produces.</summary>
    private static List<string> Lines(byte[] content)
    {
        var text = Encoding.UTF8.GetString(content).Replace("\r\n", "\n", StringComparison.Ordinal);
        var lines = text.Split('\n').ToList();
        if (lines.Count > 0 && lines[^1].Length == 0) lines.RemoveAt(lines.Count - 1);
        return lines;
    }
}
