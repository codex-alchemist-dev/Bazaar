using System.Text.Json;
using System.Text.Json.Nodes;
using Bazaar.Rubedo.Semantics;

namespace Bazaar.Rubedo.LanguageServer;

/// <summary>Command line of the tool: <c>[--socket] [--schema &lt;file&gt;]</c> (doc 41d section 5).</summary>
public sealed record ServerOptions(bool Socket, RubedoSchema? Schema)
using Bazaar.Rubedo.Views;

namespace Bazaar.Rubedo.LanguageServer;

/// <summary>Command line of the tool: <c>[--socket] [--schema &lt;file&gt;] [--tokens &lt;file&gt;]</c> (doc 41d section 5, doc 41c section 5.1).</summary>
public sealed record ServerOptions(bool Socket, RubedoSchema? Schema, IViewTokenCatalog? Tokens = null)
{
    /// <summary>Parses the arguments; on a bad argument or schema file returns false with a one-line message (exit code 2).</summary>
    public static bool TryParse(string[] args, out ServerOptions options, out string? problem)
    {
        options = new ServerOptions(false, null);
        problem = null;
        bool socket = false;
        RubedoSchema? schema = null;
        IViewTokenCatalog? tokens = null;
        for (int i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--socket":
                    socket = true;
                    break;
                case "--schema":
                    if (i + 1 >= args.Length)
                    {
                        problem = "--schema needs a file path.";
                        return false;
                    }

                    if (!TryLoadSchema(args[++i], out schema, out problem))
                    {
                        return false;
                    }

                    break;
                case "--tokens":
                    if (i + 1 >= args.Length)
                    {
                        problem = "--tokens needs a file path.";
                        return false;
                    }

                    if (!TryLoadTokens(args[++i], out tokens, out problem))
                    {
                        return false;
                    }

                    break;
                default:
                    problem = $"Unknown argument: {args[i]}";
                    return false;
            }
        }

        options = new ServerOptions(socket, schema);
        options = new ServerOptions(socket, schema, tokens);
        return true;
    }

    /// <summary>
    /// Reads a schema file: a JSON object mapping identifier names to <c>bool</c>, <c>number</c> or <c>text</c>. This is the one
    /// place the server reads a file, and only at startup, from the path the operator named.
    /// </summary>
    public static bool TryLoadSchema(string path, out RubedoSchema? schema, out string? problem)
    {
        schema = null;
        try
        {
            return TryParseSchema(File.ReadAllText(path), out schema, out problem);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            problem = $"Cannot read schema file '{path}': {ex.Message}";
            return false;
        }
    }

    /// <summary>Reads a token catalog file (doc 41c section 5.1): a JSON list of <c>{ "name": "...", "kind": "..." }</c>.</summary>
    public static bool TryLoadTokens(string path, out IViewTokenCatalog? tokens, out string? problem)
    {
        tokens = null;
        try
        {
            return TryParseTokens(File.ReadAllText(path), out tokens, out problem);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            problem = $"Cannot read tokens file '{path}': {ex.Message}";
            return false;
        }
    }

    public static bool TryParseTokens(string json, out IViewTokenCatalog? tokens, out string? problem)
    {
        tokens = null;
        JsonNode? node;
        try
        {
            node = JsonNode.Parse(json);
        }
        catch (JsonException ex)
        {
            problem = $"Tokens file is not valid JSON: {ex.Message}";
            return false;
        }

        if (node is not JsonArray list)
        {
            problem = "Tokens file must be a JSON list of { \"name\": ..., \"kind\": ... } objects.";
            return false;
        }

        var found = new Dictionary<string, TokenKind>(StringComparer.Ordinal);
        foreach (JsonNode? item in list)
        {
            string? name = item?["name"] is JsonValue n && n.TryGetValue(out string? ns) ? ns : null;
            string? kindName = item?["kind"] is JsonValue k && k.TryGetValue(out string? ks) ? ks : null;
            if (string.IsNullOrEmpty(name) || !Enum.TryParse(kindName, ignoreCase: false, out TokenKind kind) || !Enum.IsDefined(kind))
            {
                problem = $"Each token needs a name and a kind ({string.Join(", ", Enum.GetNames<TokenKind>())}); got '{name}' / '{kindName}'.";
                return false;
            }

            found[name] = kind;
        }

        tokens = new StaticViewTokenCatalog(found);
        problem = null;
        return true;
    }

    public static bool TryParseSchema(string json, out RubedoSchema? schema, out string? problem)
    {
        schema = null;
        JsonNode? node;
        try
        {
            node = JsonNode.Parse(json);
        }
        catch (JsonException ex)
        {
            problem = $"Schema file is not valid JSON: {ex.Message}";
            return false;
        }

        if (node is not JsonObject obj)
        {
            problem = "Schema file must be a JSON object mapping identifier names to bool, number or text.";
            return false;
        }

        var identifiers = new Dictionary<string, RubedoType>(StringComparer.Ordinal);
        foreach ((string name, JsonNode? value) in obj)
        {
            if (!IsIdentifier(name))
            {
                problem = $"Schema identifier '{name}' is not a valid identifier.";
                return false;
            }

            string? typeName = value is JsonValue v && v.TryGetValue(out string? s) ? s : null;
            RubedoType? type = typeName switch
            {
                "bool" => RubedoType.Bool,
                "number" => RubedoType.Number,
                "text" => RubedoType.Text,
                _ => null,
            };
            if (type is null)
            {
                problem = $"Schema identifier '{name}' has an unknown type; use bool, number or text.";
                return false;
            }

            identifiers[name] = type.Value;
        }

        schema = new RubedoSchema(identifiers);
        problem = null;
        return true;
    }

    private static bool IsIdentifier(string name) =>
        name.Length > 0 && (char.IsAsciiLetter(name[0]) || name[0] == '_') && name.All(c => char.IsAsciiLetterOrDigit(c) || c == '_');
}
