using Bazaar.Rubedo.Views;

namespace Bazaar.Rubedo.Analysis;

/// <summary>
/// Completion inside a view document (doc 41c). It replays a forgiving JSON token stream up to the cursor to learn which key or
/// value the cursor string is, and where in the document it sits. It never parses the whole document, so it works on documents
/// that are half written.
/// </summary>
internal static class ViewCompleter
{
    private enum JsonKind
    {
        LeftBrace,
        RightBrace,
        LeftBracket,
        RightBracket,
        Colon,
        Comma,
        String,
        Other,
    }

    private readonly record struct JsonToken(JsonKind Kind, int Start, int End, bool Terminated);

    private sealed class Level(bool isObject, string name)
    {
        public bool IsObject { get; } = isObject;

        public string Name { get; } = name;

        public bool ExpectKey { get; set; } = isObject;

        public string? LastKey { get; set; }

        public string? Widget { get; set; }

        public Level Snapshot() => new(IsObject, Name) { ExpectKey = ExpectKey, LastKey = LastKey, Widget = Widget };
    }

    private static readonly string[] _documentKeys = ["actions", "data", "dslVersion", "name", "root"];
    private static readonly string[] _nodeKeys = ["children", "props", "widget"];
    private static readonly string[] _actionKeys = ["kind", "target"];
    private static readonly string[] _dataKeys = ["capability", "fields", "type"];
    private static readonly string[] _commonProps = ["enabledWhen", "focusOrder", "visibleWhen"];
    private static readonly string[] _actionKinds = ["invoke", "navigate", "openPane", "setSetting"];
    private static readonly string[] _fieldTypes = ["bool", "number", "text"];
    private static readonly string[] _dataTypes = ["bool", "list", "number", "text"];

    /// <summary>Props each widget is documented to take (doc 41c sections 3 and 7), on top of the common ones.</summary>
    private static readonly Dictionary<string, string[]> _widgetProps = new(StringComparer.Ordinal)
    {
        ["text"] = ["label", "value"],
        ["badge"] = ["label", "value"],
        ["button"] = ["action", "label"],
        ["icon"] = ["label"],
        ["input"] = ["label", "value"],
        ["list"] = ["key", "over"],
        ["tab"] = ["title"],
    };

    public static CompletionList Complete(string json, int offset)
    {
        List<JsonToken> tokens = Scan(json);
        int cursor = tokens.FindIndex(t => t.Kind == JsonKind.String && t.Start < offset && (t.Terminated ? offset < t.End : offset <= t.End));
    public static CompletionList Complete(string json, int offset, IViewTokenCatalog? tokens = null)
    {
        List<JsonToken> tokens_ = Scan(json);
        int cursor = tokens_.FindIndex(t => t.Kind == JsonKind.String && t.Start < offset && (t.Terminated ? offset < t.End : offset <= t.End));
        if (cursor < 0)
        {
            return new CompletionList(offset, offset, []);
        }

        JsonToken current = tokens[cursor];
        JsonToken current = tokens_[cursor];
        int contentStart = current.Start + 1;
        int contentEnd = current.Terminated ? current.End - 1 : current.End;
        string prefix = json[contentStart..offset];

        var stack = new List<Level>();
        List<Level>? snapshot = null;
        var actionIds = new List<string>();
        for (int i = 0; i < tokens.Count; i++)
        for (int i = 0; i < tokens_.Count; i++)
        {
            if (i == cursor)
            {
                snapshot = stack.Select(l => l.Snapshot()).ToList();
            }

            Apply(json, tokens[i], stack, i == cursor ? null : actionIds);
        }

        var (kind, candidates) = Candidates(snapshot ?? [], actionIds);
            Apply(json, tokens_[i], stack, i == cursor ? null : actionIds);
        }

        var (kind, candidates) = Candidates(snapshot ?? [], actionIds, tokens);
        IEnumerable<CompletionItem> items = candidates
            .Where(c => c.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            .Order(StringComparer.Ordinal)
            .Select(c => new CompletionItem(c, kind, string.Empty, c, null, 0));
        return new CompletionList(contentStart, contentEnd, items.ToArray());
    }

    private static (CompletionKind Kind, IEnumerable<string> Items) Candidates(List<Level> stack, List<string> actionIds)
    private static (CompletionKind Kind, IEnumerable<string> Items) Candidates(List<Level> stack, List<string> actionIds, IViewTokenCatalog? tokens)
    {
        if (stack.Count == 0)
        {
            return (CompletionKind.DocumentKey, []);
        }

        Level top = stack[^1];
        Level? parent = stack.Count >= 2 ? stack[^2] : null;
        bool isNode = top.IsObject && (top.Name == "root" && stack.Count == 2 || top.Name == "[]" && parent?.Name == "children");

        if (top.IsObject && top.ExpectKey)
        {
            if (stack.Count == 1)
            {
                return (CompletionKind.DocumentKey, _documentKeys);
            }

            if (top.Name == "props")
            {
                return (CompletionKind.Property, PropsFor(stack.Count >= 2 ? stack[^2].Widget : null));
            }

            if (isNode)
            {
                return (CompletionKind.DocumentKey, _nodeKeys);
            }

            if (parent?.Name == "actions")
            {
                return (CompletionKind.DocumentKey, _actionKeys);
            }

            if (parent?.Name == "data")
            {
                return (CompletionKind.DocumentKey, _dataKeys);
            }

            return (CompletionKind.DocumentKey, []);
        }

        if (!top.IsObject)
        {
            return (CompletionKind.DocumentKey, []);
        }

        switch (top.LastKey)
        {
            case "widget" when isNode:
                bool underTabs = stack.Count >= 3 && stack[^3].Widget == "tabs";
                IEnumerable<string> widgets = Enum.GetValues<ViewWidget>().Select(ViewWidgets.WireName);
                return (CompletionKind.Widget, underTabs ? widgets.Append("tab") : widgets);
            case "action" when top.Name == "props":
                return (CompletionKind.Action, actionIds.Distinct(StringComparer.Ordinal));
            case "token" when parent?.Name == "props" && tokens is not null:
                return (CompletionKind.Token, TokensFor(top.Name, tokens));
            case "kind" when parent?.Name == "actions":
                return (CompletionKind.ActionKind, _actionKinds);
            case "type" when parent?.Name == "data":
                return (CompletionKind.DataType, _dataTypes);
            case not null when top.Name == "fields":
                return (CompletionKind.DataType, _fieldTypes);
            default:
                return (CompletionKind.DocumentKey, []);
        }
    }

    /// <summary>The prop names whose token must be of one kind (doc 41c section 5.1); every other prop takes any value token.</summary>
    private static readonly Dictionary<string, TokenKind> _propTokenKinds = new(StringComparer.Ordinal)
    {
        ["color"] = TokenKind.Colour,
        ["background"] = TokenKind.Colour,
        ["borderColor"] = TokenKind.Colour,
        ["duration"] = TokenKind.Duration,
        ["motion"] = TokenKind.Interaction,
        ["opacity"] = TokenKind.Number,
    };

    private static IEnumerable<string> TokensFor(string prop, IViewTokenCatalog tokens) =>
        tokens.Names.Where(name => tokens.KindOf(name) is { } kind &&
            (_propTokenKinds.TryGetValue(prop, out TokenKind wanted) ? kind == wanted : kind != TokenKind.Interaction));

    private static IEnumerable<string> PropsFor(string? widget)
    {
        if (widget is not null && _widgetProps.TryGetValue(widget, out string[]? known))
        {
            return known.Concat(_commonProps);
        }

        // Widget unknown (not written yet, or no extra props): offer every prop the documents define.
        IEnumerable<string> all = widget is null ? _widgetProps.Values.SelectMany(v => v) : [];
        return all.Concat(_commonProps).Distinct(StringComparer.Ordinal);
    }

    /// <summary>Feeds one token to the level stack. Collects the keys of the top-level <c>actions</c> object when asked.</summary>
    private static void Apply(string json, JsonToken token, List<Level> stack, List<string>? actionIds)
    {
        Level? top = stack.Count > 0 ? stack[^1] : null;
        switch (token.Kind)
        {
            case JsonKind.LeftBrace:
            case JsonKind.LeftBracket:
                string name = top is null ? "$" : top.IsObject ? top.LastKey ?? string.Empty : "[]";
                if (top is { IsObject: true })
                {
                    top.ExpectKey = false;
                }

                stack.Add(new Level(token.Kind == JsonKind.LeftBrace, name));
                break;
            case JsonKind.RightBrace:
            case JsonKind.RightBracket:
                if (stack.Count > 0)
                {
                    stack.RemoveAt(stack.Count - 1);
                }

                break;
            case JsonKind.Comma:
                if (top is { IsObject: true })
                {
                    top.ExpectKey = true;
                    top.LastKey = null;
                }

                break;
            case JsonKind.String:
                string content = json[(token.Start + 1)..(token.Terminated ? token.End - 1 : token.End)];
                if (top is { IsObject: true, ExpectKey: true })
                {
                    top.LastKey = content;
                    top.ExpectKey = false;
                    if (actionIds is not null && stack.Count == 2 && top.Name == "actions")
                    {
                        actionIds.Add(content);
                    }
                }
                else if (top is { IsObject: true, LastKey: "widget" })
                {
                    top.Widget = content;
                }

                break;
        }
    }

    private static List<JsonToken> Scan(string json)
    {
        var tokens = new List<JsonToken>();
        int i = 0;
        while (i < json.Length)
        {
            char c = json[i];
            int start = i;
            if (char.IsWhiteSpace(c))
            {
                i++;
                continue;
            }

            switch (c)
            {
                case '{':
                    tokens.Add(new JsonToken(JsonKind.LeftBrace, i, ++i, true));
                    break;
                case '}':
                    tokens.Add(new JsonToken(JsonKind.RightBrace, i, ++i, true));
                    break;
                case '[':
                    tokens.Add(new JsonToken(JsonKind.LeftBracket, i, ++i, true));
                    break;
                case ']':
                    tokens.Add(new JsonToken(JsonKind.RightBracket, i, ++i, true));
                    break;
                case ':':
                    tokens.Add(new JsonToken(JsonKind.Colon, i, ++i, true));
                    break;
                case ',':
                    tokens.Add(new JsonToken(JsonKind.Comma, i, ++i, true));
                    break;
                case '"':
                    i++;
                    while (i < json.Length && json[i] is not ('"' or '\n' or '\r'))
                    {
                        i += json[i] == '\\' && i + 1 < json.Length && json[i + 1] is not ('\n' or '\r') ? 2 : 1;
                    }

                    bool terminated = i < json.Length && json[i] == '"';
                    if (terminated)
                    {
                        i++;
                    }

                    tokens.Add(new JsonToken(JsonKind.String, start, Math.Min(i, json.Length), terminated));
                    break;
                default:
                    while (i < json.Length && !char.IsWhiteSpace(json[i]) && json[i] is not ('{' or '}' or '[' or ']' or ':' or ',' or '"'))
                    {
                        i++;
                    }

                    tokens.Add(new JsonToken(JsonKind.Other, start, i, true));
                    break;
            }
        }

        return tokens;
    }
}
