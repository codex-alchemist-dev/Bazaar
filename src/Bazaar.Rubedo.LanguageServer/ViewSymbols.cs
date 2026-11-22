using System.Text;
using System.Text.Json;

namespace Bazaar.Rubedo.LanguageServer;

/// <summary>
/// Document symbols for a JSON view document (doc 41c section 8): one symbol per widget node, named by its <c>widget</c> value,
/// nested like the <c>children</c> arrays. Reads the text only; a document that is not valid JSON has no symbols.
/// </summary>
public static class ViewSymbols
{
    private const int _objectSymbolKind = 19;

    private sealed class Frame(bool isArray, string? key)
    {
        public bool IsArray { get; } = isArray;

        public string? Key { get; } = key;

        public Node? Node { get; set; }
    }

    private sealed class Node(int start)
    {
        public int Start { get; } = start;

        public int End { get; set; }

        public string Name { get; set; } = "widget";

        public List<Node> Children { get; } = [];
    }

    public static IReadOnlyList<LspDocumentSymbol> Read(string text)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(text);
        var reader = new Utf8JsonReader(bytes);
        var stack = new List<Frame>();
        var roots = new List<Node>();
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
                        {
                            var frame = new Frame(isArray: false, key);
                            frame.Node = NodeFor(stack, key, CharAt(reader.TokenStartIndex), roots);
                            stack.Add(frame);
                            key = null;
                            break;
                        }

                    case JsonTokenType.StartArray:
                        stack.Add(new Frame(isArray: true, key));
                        key = null;
                        break;
                    case JsonTokenType.EndObject:
                    case JsonTokenType.EndArray:
                        {
                            Frame frame = stack[^1];
                            stack.RemoveAt(stack.Count - 1);
                            if (frame.Node is { } node)
                            {
                                node.End = CharAt(reader.BytesConsumed);
                            }

                            break;
                        }

                    case JsonTokenType.String:
                        if (stack.Count > 0 && stack[^1] is { IsArray: false, Node: { } owner } && key == "widget")
                        {
                            owner.Name = reader.GetString() ?? owner.Name;
                        }

                        key = null;
                        break;
                    default:
                        key = null;
                        break;
                }
            }
        }
        catch (JsonException)
        {
            return [];
        }

        return roots.Select(n => ToSymbol(text, n)).ToList();
    }

    // A node is the object under the top-level "root" key, or an object in a node's "children" array.
    private static Node? NodeFor(List<Frame> stack, string? key, int start, List<Node> roots)
    {
        if (stack.Count == 1 && !stack[0].IsArray && key == "root")
        {
            var root = new Node(start);
            roots.Add(root);
            return root;
        }

        if (stack.Count >= 2 && stack[^1] is { IsArray: true, Key: "children" } && stack[^2].Node is { } parent)
        {
            var child = new Node(start);
            parent.Children.Add(child);
            return child;
        }

        return null;
    }

    private static LspDocumentSymbol ToSymbol(string text, Node node)
    {
        LspRange range = TextPositions.ToRange(text, node.Start, node.End);
        return new LspDocumentSymbol(node.Name, _objectSymbolKind, range, range, node.Children.Select(c => ToSymbol(text, c)).ToList());
    }
}
