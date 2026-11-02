using System.Text;

namespace Bazaar.Engines.Godot.SceneDiff;

/// <summary>A node section. Header holds "@type" and "@instance" when written; property values are raw text.</summary>
internal sealed record ParsedNode(string Path, IReadOnlyDictionary<string, string> Header, IReadOnlyDictionary<string, string> Properties);

/// <summary>An ext_resource, sub_resource or [resource] section. The main section has Kind "resource" and an empty Id.</summary>
internal sealed record ParsedResource(string Kind, string Id, IReadOnlyDictionary<string, string> Header, IReadOnlyDictionary<string, string> Properties);

internal sealed record ParsedScene(
    IReadOnlyDictionary<string, ParsedNode> Nodes,
    IReadOnlyDictionary<(string Kind, string Id), ParsedResource> Resources);

/// <summary>A section header: its kind word and name=value attributes with values unquoted.</summary>
internal sealed record SectionHeader(string Kind, int Line, IReadOnlyDictionary<string, string> Attributes)
{
    public string? Get(string name) => Attributes.GetValueOrDefault(name);
}

/// <summary>
/// Minimal reader for the Godot 4 text resource format (.tscn and .tres). It recognises section headers, node paths
/// and key = value lines. Values are kept as raw text and never evaluated. A value that spans lines is one value,
/// found by bracket, brace and quote balance.
/// </summary>
internal static class GodotTextParser
{
    /// <summary>The path of the scene root node.</summary>
    public const string RootPath = ".";

    /// <summary>Parses one file. Throws <see cref="SceneParseException"/> with a 1-based line number on bad input.</summary>
    public static ParsedScene Parse(string text)
    {
        ArgumentNullException.ThrowIfNull(text);

        string[] lines = text.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
        var nodes = new Dictionary<string, ParsedNode>(StringComparer.Ordinal);
        var resources = new Dictionary<(string Kind, string Id), ParsedResource>();
        bool inSection = false;
        // Property sink for the current section; null when the section kind is not diffed.
        Dictionary<string, string>? properties = null;

        int index = 0;
        while (index < lines.Length)
        {
            string trimmed = lines[index].Trim();
            if (trimmed.Length == 0 || trimmed[0] == ';')
            {
                index++;
                continue;
            }

            int lineNumber = index + 1;
            string statement = ReadStatement(lines, ref index);
            if (statement[0] == '[')
            {
                SectionHeader header = ParseHeader(statement, lineNumber);
                properties = RegisterSection(header, nodes, resources);
                inSection = true;
            }
            else
            {
                if (!inSection)
                {
                    throw new SceneParseException(lineNumber, "property line appears before any section header");
                }

                ParseProperty(statement, lineNumber, properties);
            }
        }

        return new ParsedScene(nodes, resources);
    }

    /// <summary>Reads one logical statement: physical lines joined until brackets and quotes balance.</summary>
    private static string ReadStatement(string[] lines, ref int index)
    {
        int start = index;
        var text = new StringBuilder();
        var closers = new Stack<char>();
        bool inString = false;
        while (index < lines.Length)
        {
            string line = lines[index];
            if (index > start)
            {
                text.Append('\n');
            }

            text.Append(line);
            ScanLine(line, index + 1, closers, ref inString);
            index++;
            if (closers.Count == 0 && !inString)
            {
                return text.ToString().Trim();
            }
        }

        throw new SceneParseException(start + 1, "statement is never closed (unbalanced brackets or quotes)");
    }

    private static void ScanLine(string line, int lineNumber, Stack<char> closers, ref bool inString)
    {
        for (int i = 0; i < line.Length; i++)
        {
            char c = line[i];
            if (inString)
            {
                if (c == '\\')
                {
                    i++;
                }
                else if (c == '"')
                {
                    inString = false;
                }
            }
            else if (c == '"')
            {
                inString = true;
            }
            else if (c is '(' or '[' or '{')
            {
                closers.Push(Closer(c));
            }
            else if (c is ')' or ']' or '}')
            {
                if (closers.Count == 0 || closers.Pop() != c)
                {
                    throw new SceneParseException(lineNumber, $"unexpected '{c}'");
                }
            }
        }
    }

    private static char Closer(char open) => open switch
    {
        '(' => ')',
        '[' => ']',
        _ => '}',
    };

    private static SectionHeader ParseHeader(string statement, int line)
    {
        var attributes = new Dictionary<string, string>(StringComparer.Ordinal);
        int pos = 1;
        string kind = ReadWord(statement, ref pos);
        if (kind.Length == 0)
        {
            throw new SceneParseException(line, "section header has no name");
        }

        while (true)
        {
            SkipWhitespace(statement, ref pos);
            if (pos >= statement.Length)
            {
                throw new SceneParseException(line, "section header is not closed with ']'");
            }

            if (statement[pos] == ']')
            {
                if (pos != statement.Length - 1)
                {
                    throw new SceneParseException(line, "unexpected text after section header");
                }

                break;
            }

            string name = ReadWord(statement, ref pos);
            if (name.Length == 0 || pos >= statement.Length || statement[pos] != '=')
            {
                throw new SceneParseException(line, "expected name=value in section header");
            }

            pos++;
            string value = ReadValue(statement, ref pos, line);
            if (!attributes.TryAdd(name, value))
            {
                throw new SceneParseException(line, $"duplicate attribute '{name}'");
            }
        }

        return new SectionHeader(kind, line, attributes);
    }

    private static string ReadWord(string text, ref int pos)
    {
        int start = pos;
        while (pos < text.Length && !char.IsWhiteSpace(text[pos]) && text[pos] is not ('=' or ']'))
        {
            pos++;
        }

        return text[start..pos];
    }

    private static void SkipWhitespace(string text, ref int pos)
    {
        while (pos < text.Length && char.IsWhiteSpace(text[pos]))
        {
            pos++;
        }
    }

    /// <summary>Reads a header value: a quoted string (unescaped) or a bare token such as ExtResource("1_x") (raw).</summary>
    private static string ReadValue(string text, ref int pos, int line)
    {
        if (pos < text.Length && text[pos] == '"')
        {
            int end = SkipQuoted(text, pos, line);
            string value = Unescape(text[(pos + 1)..(end - 1)]);
            pos = end;
            return value;
        }

        int start = pos;
        var closers = new Stack<char>();
        while (pos < text.Length)
        {
            char c = text[pos];
            if (closers.Count == 0 && (char.IsWhiteSpace(c) || c == ']'))
            {
                break;
            }

            if (c == '"')
            {
                pos = SkipQuoted(text, pos, line);
                continue;
            }

            if (c is '(' or '[' or '{')
            {
                closers.Push(Closer(c));
            }
            else if (c is ')' or ']' or '}')
            {
                if (closers.Count == 0 || closers.Pop() != c)
                {
                    throw new SceneParseException(line, $"unexpected '{c}' in attribute value");
                }
            }

            pos++;
        }

        if (pos == start)
        {
            throw new SceneParseException(line, "empty attribute value");
        }

        return text[start..pos];
    }

    /// <summary>Returns the index just past the closing quote of the string that starts at <paramref name="pos"/>.</summary>
    private static int SkipQuoted(string text, int pos, int line)
    {
        int i = pos + 1;
        while (i < text.Length)
        {
            if (text[i] == '\\')
            {
                i += 2;
            }
            else if (text[i] == '"')
            {
                return i + 1;
            }
            else
            {
                i++;
            }
        }

        throw new SceneParseException(line, "unterminated quoted string");
    }

    private static string Unescape(string raw)
    {
        var sb = new StringBuilder(raw.Length);
        for (int i = 0; i < raw.Length; i++)
        {
            if (raw[i] == '\\' && i + 1 < raw.Length)
            {
                char next = raw[++i];
                sb.Append(next switch
                {
                    'n' => '\n',
                    't' => '\t',
                    _ => next,
                });
            }
            else
            {
                sb.Append(raw[i]);
            }
        }

        return sb.ToString();
    }

    /// <summary>Handles a section header and returns the property sink for its body, or null when the section is not diffed.</summary>
    private static Dictionary<string, string>? RegisterSection(
        SectionHeader header,
        Dictionary<string, ParsedNode> nodes,
        Dictionary<(string Kind, string Id), ParsedResource> resources)
    {
        switch (header.Kind)
        {
            case "node":
                {
                    string path = NodePath(Required(header, "name"), header.Get("parent"));
                    if (nodes.ContainsKey(path))
                    {
                        throw new SceneParseException(header.Line, $"duplicate node path '{path}'");
                    }

                    var properties = new Dictionary<string, string>(StringComparer.Ordinal);
                    nodes.Add(path, new ParsedNode(path, HeaderValues(header, "type", "instance"), properties));
                    return properties;
                }

            case "ext_resource":
                return AddResource(resources, header, "ext_resource", Required(header, "id"), HeaderValues(header, "type", "path"));

            case "sub_resource":
                return AddResource(resources, header, "sub_resource", Required(header, "id"), HeaderValues(header, "type"));

            case "resource":
                return AddResource(resources, header, "resource", string.Empty, HeaderValues(header));

            default:
                // gd_scene, gd_resource, connection, editable and other sections are not diffed.
                return null;
        }
    }

    private static Dictionary<string, string> AddResource(
        Dictionary<(string Kind, string Id), ParsedResource> resources,
        SectionHeader header,
        string kind,
        string id,
        Dictionary<string, string> headerValues)
    {
        var key = (kind, id);
        if (resources.ContainsKey(key))
        {
            throw new SceneParseException(header.Line, $"duplicate {kind} '{id}'");
        }

        var properties = new Dictionary<string, string>(StringComparer.Ordinal);
        resources.Add(key, new ParsedResource(kind, id, headerValues, properties));
        return properties;
    }

    private static string Required(SectionHeader header, string name) =>
        header.Get(name) ?? throw new SceneParseException(header.Line, $"[{header.Kind}] has no {name} attribute");

    private static Dictionary<string, string> HeaderValues(SectionHeader header, params string[] names)
    {
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (string name in names)
        {
            string? value = header.Get(name);
            if (value is not null)
            {
                values.Add("@" + name, value);
            }
        }

        return values;
    }

    /// <summary>Parent path plus name. A node without a parent is the root, "."; children of the root have no prefix.</summary>
    private static string NodePath(string name, string? parent) => parent switch
    {
        null => RootPath,
        RootPath => name,
        _ => $"{parent}/{name}",
    };

    private static void ParseProperty(string statement, int line, Dictionary<string, string>? properties)
    {
        int eq = statement.IndexOf('=');
        if (eq < 0)
        {
            throw new SceneParseException(line, "expected 'key = value'");
        }

        string key = statement[..eq].Trim();
        if (key.Length == 0 || key.Any(char.IsWhiteSpace))
        {
            throw new SceneParseException(line, "invalid property key");
        }

        string value = statement[(eq + 1)..].Trim();
        if (value.Length == 0)
        {
            throw new SceneParseException(line, $"property '{key}' has no value");
        }

        if (properties is null)
        {
            return;
        }

        if (!properties.TryAdd(key, value))
        {
            throw new SceneParseException(line, $"duplicate property '{key}'");
        }
    }
}
