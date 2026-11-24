using System.Text;
using System.Text.RegularExpressions;

namespace Bazaar.Server.Memory.Notes;

/// <summary>
/// A value in the front matter subset: a scalar, a flow sequence or a flow mapping. Equality is reference
/// equality on the collections; compare notes through their canonical written form instead.
/// </summary>
public abstract record FrontValue;

/// <summary>A scalar, stored as its text after quote removal.</summary>
/// <param name="Text">The scalar text.</param>
public sealed record FrontScalar(string Text) : FrontValue;

/// <summary>A flow sequence such as <c>[a, b]</c>.</summary>
/// <param name="Items">The items in source order.</param>
public sealed record FrontSequence(IReadOnlyList<FrontValue> Items) : FrontValue;

/// <summary>A flow mapping such as <c>{kind: procedural}</c>. Entries keep their source order.</summary>
/// <param name="Entries">The key and value pairs in source order.</param>
public sealed record FrontMapping(IReadOnlyList<KeyValuePair<string, FrontValue>> Entries) : FrontValue;

/// <summary>
/// Reads and writes the YAML subset used by note front matter (docs/design/08-memory-system.md, section 1).
/// Top-level lines are <c>key: value</c>. A value is a plain scalar (top level: the rest of the line),
/// a double-quoted scalar, a flow sequence <c>[a, b]</c> or a flow mapping <c>{k: v}</c>, nestable.
/// Block mappings, indentation, anchors, tags and multi-line scalars are rejected. Blank lines and
/// <c>#</c> comment lines are skipped. Nothing here throws on bad input.
/// </summary>
internal static partial class FrontMatterCodec
{
    private const int _maxDepth = 8;

    [GeneratedRegex("^[A-Za-z_][A-Za-z0-9_-]*$")]
    private static partial Regex KeyRegex();

    [GeneratedRegex("^[A-Za-z0-9_-]+$")]
    private static partial Regex SafeKeyRegex();

    [GeneratedRegex("^[A-Za-z0-9_./@+:-]+$")]
    private static partial Regex SafeScalarRegex();

    /// <summary>Returns the next line without its line break (LF or CRLF) and advances <paramref name="pos"/>.</summary>
    public static string NextLine(string text, ref int pos)
    {
        var end = text.IndexOf('\n', pos);
        var stop = end < 0 ? text.Length : end;
        var line = text[pos..stop];
        pos = end < 0 ? text.Length : end + 1;
        return line.EndsWith('\r') ? line[..^1] : line;
    }

    /// <summary>Parses the text between the two <c>---</c> lines into ordered top-level entries.</summary>
    public static bool TryParseLines(
        string front,
        out List<KeyValuePair<string, FrontValue>> entries,
        out NoteParseError error,
        out string detail)
    {
        entries = [];
        error = default;
        detail = string.Empty;

        var pos = 0;
        while (pos < front.Length)
        {
            var line = NextLine(front, ref pos);
            var trimmed = line.Trim();
            if (trimmed.Length == 0 || trimmed[0] == '#')
            {
                continue;
            }

            if (char.IsWhiteSpace(line[0]))
            {
                return Fail(NoteParseError.UnsupportedSyntax, "Indented lines (nested block mappings) are not supported.", out error, out detail);
            }

            var colon = line.IndexOf(':', StringComparison.Ordinal);
            if (colon <= 0)
            {
                return Fail(NoteParseError.MalformedLine, "A front matter line is not key: value.", out error, out detail);
            }

            var key = line[..colon];
            if (!KeyRegex().IsMatch(key))
            {
                return Fail(NoteParseError.MalformedLine, "A front matter key has invalid characters.", out error, out detail);
            }

            if (entries.Exists(e => string.Equals(e.Key, key, StringComparison.Ordinal)))
            {
                return Fail(NoteParseError.DuplicateKey, $"Duplicate front matter key '{key}'.", out error, out detail);
            }

            var raw = line[(colon + 1)..];
            if (raw.Trim().Length == 0)
            {
                return Fail(NoteParseError.MalformedValue, $"'{key}' has no value; block mappings are not supported.", out error, out detail);
            }

            if (!TryParseTopLevel(raw, out var value, out var why))
            {
                return Fail(NoteParseError.MalformedValue, $"'{key}': {why}", out error, out detail);
            }

            entries.Add(KeyValuePair.Create(key, value!));
        }

        return true;
    }

    /// <summary>Parses one top-level value. A plain scalar takes the rest of the line, so titles may contain colons.</summary>
    public static bool TryParseTopLevel(string raw, out FrontValue? value, out string? error)
    {
        var text = raw.Trim();
        value = null;
        error = null;
        if (text.Length == 0)
        {
            error = "missing value";
            return false;
        }

        if (text[0] is not ('[' or '{' or '"'))
        {
            value = new FrontScalar(text);
            return true;
        }

        var reader = new FlowReader(text);
        var parsed = reader.ReadValue(0);
        if (parsed is null || !reader.AtEnd)
        {
            error = reader.Error ?? "unexpected text after value";
            return false;
        }

        value = parsed;
        return true;
    }

    /// <summary>Writes a value in the subset. Scalars are plain when safe, otherwise double-quoted.</summary>
    public static string WriteValue(FrontValue value) => value switch
    {
        FrontScalar scalar => WriteScalar(scalar.Text),
        FrontSequence sequence => "[" + string.Join(", ", sequence.Items.Select(WriteValue)) + "]",
        FrontMapping mapping => "{" + string.Join(", ", mapping.Entries.Select(e => WriteKey(e.Key) + ": " + WriteValue(e.Value))) + "}",
        _ => throw new ArgumentOutOfRangeException(nameof(value), value, "Unknown front matter value."),
    };

    private static string WriteScalar(string text) =>
        text.Length > 0 && SafeScalarRegex().IsMatch(text) ? text : Quote(text);

    private static string WriteKey(string key) => SafeKeyRegex().IsMatch(key) ? key : Quote(key);

    private static string Quote(string text) =>
        "\"" + text
            .Replace("\\", "\\\\", StringComparison.Ordinal)
            .Replace("\"", "\\\"", StringComparison.Ordinal)
            .Replace("\n", "\\n", StringComparison.Ordinal)
            .Replace("\r", "\\r", StringComparison.Ordinal)
            .Replace("\t", "\\t", StringComparison.Ordinal) + "\"";

    private static bool Fail(NoteParseError code, string message, out NoteParseError error, out string detail)
    {
        error = code;
        detail = message;
        return false;
    }

    /// <summary>Recursive-descent reader for flow collections and quoted scalars.</summary>
    private sealed class FlowReader(string text)
    {
        private int _pos;

        public string? Error { get; private set; }

        /// <summary>True when only whitespace is left.</summary>
        public bool AtEnd
        {
            get
            {
                SkipSpaces();
                return _pos >= text.Length;
            }
        }

        private char Peek => _pos < text.Length ? text[_pos] : '\0';

        public FrontValue? ReadValue(int depth)
        {
            if (depth > _maxDepth)
            {
                return Fail("nesting is too deep");
            }

            SkipSpaces();
            if (_pos >= text.Length)
            {
                return Fail("missing value");
            }

            switch (text[_pos])
            {
                case '[':
                    return ReadSequence(depth);
                case '{':
                    return ReadMapping(depth);
                case '"':
                    var quoted = ReadQuoted();
                    return quoted is null ? null : new FrontScalar(quoted);
                default:
                    var plain = ReadPlain(",]}");
                    return plain.Length == 0 ? Fail("missing value") : new FrontScalar(plain);
            }
        }

        private FrontValue? ReadSequence(int depth)
        {
            _pos++;
            var items = new List<FrontValue>();
            SkipSpaces();
            if (Peek == ']')
            {
                _pos++;
                return new FrontSequence(items);
            }

            while (true)
            {
                var item = ReadValue(depth + 1);
                if (item is null)
                {
                    return null;
                }

                items.Add(item);
                SkipSpaces();
                if (Peek == ',')
                {
                    _pos++;
                    continue;
                }

                if (Peek == ']')
                {
                    _pos++;
                    return new FrontSequence(items);
                }

                return Fail("expected ',' or ']'");
            }
        }

        private FrontValue? ReadMapping(int depth)
        {
            _pos++;
            var entries = new List<KeyValuePair<string, FrontValue>>();
            SkipSpaces();
            if (Peek == '}')
            {
                _pos++;
                return new FrontMapping(entries);
            }

            while (true)
            {
                SkipSpaces();
                var key = Peek == '"' ? ReadQuoted() : ReadPlain(":,}");
                if (key is null)
                {
                    return null;
                }

                if (key.Length == 0)
                {
                    return Fail("empty mapping key");
                }

                if (entries.Exists(e => string.Equals(e.Key, key, StringComparison.Ordinal)))
                {
                    return Fail("duplicate key in mapping");
                }

                SkipSpaces();
                if (Peek != ':')
                {
                    return Fail("expected ':' after key");
                }

                _pos++;
                var value = ReadValue(depth + 1);
                if (value is null)
                {
                    return null;
                }

                entries.Add(KeyValuePair.Create(key, value));
                SkipSpaces();
                if (Peek == ',')
                {
                    _pos++;
                    continue;
                }

                if (Peek == '}')
                {
                    _pos++;
                    return new FrontMapping(entries);
                }

                return Fail("expected ',' or '}'");
            }
        }

        private string? ReadQuoted()
        {
            _pos++;
            var sb = new StringBuilder();
            while (_pos < text.Length)
            {
                var c = text[_pos++];
                if (c == '"')
                {
                    return sb.ToString();
                }

                if (c != '\\')
                {
                    sb.Append(c);
                    continue;
                }

                if (_pos >= text.Length)
                {
                    break;
                }

                var escaped = text[_pos++];
                switch (escaped)
                {
                    case '\\': sb.Append('\\'); break;
                    case '"': sb.Append('"'); break;
                    case 'n': sb.Append('\n'); break;
                    case 'r': sb.Append('\r'); break;
                    case 't': sb.Append('\t'); break;
                    default:
                        Fail("unsupported escape in quoted scalar");
                        return null;
                }
            }

            Fail("unterminated quoted scalar");
            return null;
        }

        private string ReadPlain(string stops)
        {
            var start = _pos;
            while (_pos < text.Length && stops.IndexOf(text[_pos], StringComparison.Ordinal) < 0)
            {
                _pos++;
            }

            return text[start.._pos].TrimEnd();
        }

        private void SkipSpaces()
        {
            while (_pos < text.Length && text[_pos] is ' ' or '\t')
            {
                _pos++;
            }
        }

        private FrontValue? Fail(string message)
        {
            Error ??= message;
            return null;
        }
    }
}
