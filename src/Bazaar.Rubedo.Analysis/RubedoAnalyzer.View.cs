using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Bazaar.Rubedo.Views;

namespace Bazaar.Rubedo.Analysis;

public static partial class RubedoAnalyzer
{
    /// <summary>
    /// Reads and validates a JSON view document (doc 41c section 8) with the existing reader and validator. A JSON syntax
    /// problem is reported at its position (<c>view.json</c>), a shape problem as <c>view.shape</c>, and each validator problem
    /// with its own code. Validator problems are placed on the widget name of the node they name when the node can be found,
    /// else on the first character. An empty list means the document is valid.
    /// </summary>
    public static IReadOnlyList<AnalysisDiagnostic> DiagnoseView(string? json)
    public static IReadOnlyList<AnalysisDiagnostic> DiagnoseView(string? json) => DiagnoseView(json, null);

    /// <summary>As <see cref="DiagnoseView(string?)"/>, also checking token references against <paramref name="tokens"/> (doc 41c section 5.1).</summary>
    public static IReadOnlyList<AnalysisDiagnostic> DiagnoseView(string? json, IViewTokenCatalog? tokens)
    {
        json ??= string.Empty;
        if (IsOverCap(json))
        {
            return [TooLarge(json.Length)];
        }

        ViewDocument document;
        try
        {
            document = ViewDocumentReader.Read(json);
        }
        catch (JsonException ex)
        {
            int at = ex.LineNumber is { } line && ex.BytePositionInLine is { } column ? OffsetOf(json, line, column) : 0;
            return [new AnalysisDiagnostic("view.json", ex.Message, at, Math.Min(json.Length, at + 1))];
        }
        catch (Exception ex) when (ex is FormatException or InvalidOperationException or ArgumentException or OverflowException)
        {
            return [new AnalysisDiagnostic("view.shape", ex.Message, 0, Math.Min(json.Length, 1))];
        }

        IReadOnlyList<RubedoDiagnostic> problems = ViewValidator.Validate(document);
        IReadOnlyList<RubedoDiagnostic> problems = ViewValidator.Validate(document, tokens);
        if (problems.Count == 0)
        {
            return [];
        }

        Dictionary<string, (int Start, int End)> locations = ViewLocator.Locate(json);
        var result = new List<AnalysisDiagnostic>(problems.Count);
        foreach (RubedoDiagnostic problem in problems)
        {
            (int start, int end) = LocateProblem(problem, locations);
            result.Add(new AnalysisDiagnostic(problem.Code, problem.Message, start, end));
        }

        return result;
    }

    /// <summary>
    /// Completions inside a JSON view document, for the string the cursor is in: widget names (and <c>tab</c> under
    /// <c>tabs</c>), prop names (narrowed by the node's widget when it is already written), action ids in a button's
    /// <c>action</c>, action kinds, data types and the document's own keys. Nothing outside a string.
    /// </summary>
    public static CompletionList CompleteView(string? json, int offset)
    public static CompletionList CompleteView(string? json, int offset) => CompleteView(json, offset, null);

    /// <summary>
    /// As <see cref="CompleteView(string?, int)"/>, and inside <c>{ "token": "..." }</c> in a prop it also offers the names in
    /// <paramref name="tokens"/>, narrowed to the kind the prop takes (doc 41c section 5.1).
    /// </summary>
    public static CompletionList CompleteView(string? json, int offset, IViewTokenCatalog? tokens)
    {
        json ??= string.Empty;
        offset = Math.Clamp(offset, 0, json.Length);
        if (IsOverCap(json))
        {
            return new CompletionList(offset, offset, []);
        }

        return ViewCompleter.Complete(json, offset);
        return ViewCompleter.Complete(json, offset, tokens);
    }

    private static (int Start, int End) LocateProblem(RubedoDiagnostic problem, Dictionary<string, (int Start, int End)> locations)
    {
        if (problem.Code == "view.dsl_version" && locations.TryGetValue("$dslVersion", out var version))
        {
            return version;
        }

        if (problem.Code == "view.bad_name" && locations.TryGetValue("$name", out var name))
        {
            return name;
        }

        Match match = NodePath().Match(problem.Message);
        if (match.Success && locations.TryGetValue(match.Value, out var node))
        {
            return node;
        }

        return (0, 0);
    }

    [GeneratedRegex(@"^root(/\d+)*")]
    private static partial Regex NodePath();

    /// <summary>Converts a UTF-8 line and byte column from the JSON reader into a character offset.</summary>
    private static int OffsetOf(string text, long line, long byteColumn)
    {
        int i = 0;
        for (long l = 0; l < line && i < text.Length; i++)
        {
            if (text[i] == '\n')
            {
                l++;
            }
        }

        long bytes = 0;
        while (i < text.Length && bytes < byteColumn && text[i] != '\n')
        {
            int length = char.IsHighSurrogate(text[i]) && i + 1 < text.Length ? 2 : 1;
            bytes += Encoding.UTF8.GetByteCount(text.AsSpan(i, length));
            i += length;
        }

        return i;
    }

    /// <summary>
    /// Finds node widget values and the top-level <c>dslVersion</c> and <c>name</c> in a document that already parsed. Keys are
    /// node paths in the validator's form (<c>root</c>, <c>root/0</c>, <c>root/0/1</c>) and <c>$dslVersion</c>, <c>$name</c>.
    /// </summary>
    private static class ViewLocator
    {
        private sealed class Container(bool isArray, string segment)
        {
            public bool IsArray { get; } = isArray;

            public string Segment { get; } = segment;

            public int Index { get; set; }
        }

        public static Dictionary<string, (int Start, int End)> Locate(string json)
        {
            var found = new Dictionary<string, (int, int)>(StringComparer.Ordinal);
            byte[] bytes = Encoding.UTF8.GetBytes(json);
            var reader = new Utf8JsonReader(bytes);
            var stack = new List<Container>();
            string? key = null;
            int lastByte = 0;
            int lastChar = 0;

            int CharAt(long byteIndex)
            {
                lastChar += Encoding.UTF8.GetCharCount(bytes, lastByte, (int)byteIndex - lastByte);
                lastByte = (int)byteIndex;
                return lastChar;
            }

            try
            {
                while (reader.Read())
                {
                    switch (reader.TokenType)
                    {
                        case JsonTokenType.PropertyName:
                            key = reader.GetString();
                            break;
                        case JsonTokenType.StartObject:
                        case JsonTokenType.StartArray:
                            stack.Add(new Container(reader.TokenType == JsonTokenType.StartArray, NextSegment(stack, ref key)));
                            break;
                        case JsonTokenType.EndObject:
                        case JsonTokenType.EndArray:
                            stack.RemoveAt(stack.Count - 1);
                            break;
                        default:
                            if (stack.Count > 0 && reader.TokenType is JsonTokenType.String or JsonTokenType.Number && !stack[^1].IsArray)
                            {
                                Record(found, stack, key, reader.TokenType, reader.TokenStartIndex, reader.BytesConsumed, CharAt);
                            }

                            NextSegment(stack, ref key);
                            break;
                    }
                }
            }
            catch (JsonException)
            {
                // The document parsed once already; a failure here only means fewer locations.
            }

            return found.ToDictionary(p => p.Key, p => p.Value, StringComparer.Ordinal);
        }

        private static string NextSegment(List<Container> stack, ref string? key)
        {
            if (stack.Count == 0)
            {
                return "$";
            }

            Container parent = stack[^1];
            if (parent.IsArray)
            {
                return (parent.Index++).ToString(CultureInfo.InvariantCulture);
            }

            string segment = key ?? string.Empty;
            key = null;
            return segment;
        }

        private static void Record(
            Dictionary<string, (int, int)> found,
            List<Container> stack,
            string? key,
            JsonTokenType token,
            long startByte,
            long endByte,
            Func<long, int> charAt)
        {
            if (stack.Count == 1 && key is "dslVersion" or "name")
            {
                found[$"${key}"] = (charAt(startByte), charAt(endByte));
                return;
            }

            if (key != "widget" || token != JsonTokenType.String || stack.Count < 2 || stack[1].Segment != "root")
            {
                return;
            }

            // The object holding "widget" must be root or root/children/N/children/M...
            var path = new StringBuilder("root");
            for (int i = 2; i < stack.Count; i += 2)
            {
                if (stack[i].Segment != "children" || i + 1 >= stack.Count || stack[i + 1].Segment.Length == 0 || !char.IsAsciiDigit(stack[i + 1].Segment[0]))
                {
                    return;
                }

                path.Append('/').Append(stack[i + 1].Segment);
            }

            found[path.ToString()] = (charAt(startByte), charAt(endByte));
        }
    }
}
