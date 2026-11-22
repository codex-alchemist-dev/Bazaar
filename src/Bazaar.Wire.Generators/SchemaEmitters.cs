using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace Bazaar.Wire.Generators;

/// <summary>Renders the published schema artifacts (doc 90 section 5) as deterministic text with LF line ends.</summary>
internal static class SchemaEmitters
{
    private const string UuidPattern = "^[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}$";

    /// <summary>Computes the schema hash: SHA-256 of <c>schema.json</c> rendered with an empty hash value.</summary>
    public static string ComputeHash(SchemaDoc doc)
    {
        var text = SchemaJson(doc, "");
        using var sha = SHA256.Create();
        var bytes = sha.ComputeHash(new UTF8Encoding(false).GetBytes(text));
        var sb = new StringBuilder("sha256:");
        foreach (var b in bytes) sb.Append(b.ToString("x2", CultureInfo.InvariantCulture));
        return sb.ToString();
    }

    public static string Quote(string s)
    {
        var sb = new StringBuilder("\"");
        foreach (var c in s)
        {
            switch (c)
            {
                case '"': sb.Append("\\\""); break;
                case '\\': sb.Append("\\\\"); break;
                case '\n': sb.Append("\\n"); break;
                case '\r': sb.Append("\\r"); break;
                case '\t': sb.Append("\\t"); break;
                default:
                    if (c < 0x20 || c > 0x7e) sb.Append("\\u").Append(((int)c).ToString("x4", CultureInfo.InvariantCulture));
                    else sb.Append(c);
                    break;
            }
        }

        return sb.Append('"').ToString();
    }

    private static string Bool(bool b) => b ? "true" : "false";

    public static string SchemaJson(SchemaDoc doc, string hash)
    {
        var sb = new StringBuilder();
        sb.Append("{\n");
        sb.Append("  \"protocol\": { \"major\": ").Append(doc.Major.ToString(CultureInfo.InvariantCulture)).Append(", \"minor\": ").Append(doc.Minor.ToString(CultureInfo.InvariantCulture)).Append(" },\n");
        sb.Append("  \"hash\": ").Append(Quote(hash)).Append(",\n");
        sb.Append("  \"messages\": [\n");
        for (var i = 0; i < doc.Messages.Count; i++)
        {
            var m = doc.Messages[i];
            sb.Append("    { \"type\": ").Append(Quote(m.Type)).Append(", \"payload\": ").Append(Quote(m.Payload)).Append(" }").Append(i + 1 < doc.Messages.Count ? ",\n" : "\n");
        }

        sb.Append("  ],\n  \"types\": [\n");
        for (var i = 0; i < doc.Types.Count; i++)
        {
            var t = doc.Types[i];
            sb.Append("    {\n");
            sb.Append("      \"name\": ").Append(Quote(t.Name)).Append(",\n");
            sb.Append("      \"kind\": ").Append(t.IsEnum ? "\"enum\"" : "\"record\"").Append(",\n");
            if (t.Payload) sb.Append("      \"payload\": true,\n");
            if (t.IsEnum)
            {
                if (t.Fallback is not null) sb.Append("      \"fallback\": ").Append(Quote(t.Fallback)).Append(",\n");
                sb.Append("      \"members\": [\n");
                for (var j = 0; j < t.Members.Count; j++)
                {
                    sb.Append("        { \"name\": ").Append(Quote(t.Members[j].Name)).Append(", \"value\": ").Append(t.Members[j].Value.ToString(CultureInfo.InvariantCulture)).Append(" }").Append(j + 1 < t.Members.Count ? ",\n" : "\n");
                }
            }
            else
            {
                sb.Append("      \"fields\": [\n");
                for (var j = 0; j < t.Fields.Count; j++)
                {
                    var f = t.Fields[j];
                    sb.Append("        { \"n\": ").Append(f.Position.ToString(CultureInfo.InvariantCulture))
                        .Append(", \"name\": ").Append(Quote(f.Name))
                        .Append(", \"type\": ").Append(Quote(f.Type))
                        .Append(", \"nullable\": ").Append(Bool(f.Nullable))
                        .Append(", \"default\": ").Append(Bool(f.HasDefault)).Append(" }")
                        .Append(j + 1 < t.Fields.Count ? ",\n" : "\n");
                }
            }

            sb.Append("      ]\n    }").Append(i + 1 < doc.Types.Count ? ",\n" : "\n");
        }

        sb.Append("  ]\n}\n");
        return sb.ToString();
    }

    // ---- type expression helpers -------------------------------------------------------------------------------

    private static string Core(string type, out bool nullable)
    {
        nullable = type.EndsWith("?", StringComparison.Ordinal);
        return nullable ? type.Substring(0, type.Length - 1) : type;
    }

    private static bool IsList(string core, out string element)
    {
        if (core.StartsWith("list<", StringComparison.Ordinal) && core.EndsWith(">", StringComparison.Ordinal))
        {
            element = core.Substring(5, core.Length - 6);
            return true;
        }

        element = "";
        return false;
    }

    // ---- JSON Schema -------------------------------------------------------------------------------------------

    public static string JsonSchema(SchemaDoc doc, string hash)
    {
        var sb = new StringBuilder();
        sb.Append("{\n");
        sb.Append("  \"$schema\": \"https://json-schema.org/draft/2020-12/schema\",\n");
        sb.Append("  \"title\": \"Bazaar wire payloads\",\n");
        sb.Append("  \"description\": ").Append(Quote($"Payload shapes for the JSON and CBOR codecs, protocol {doc.Major}.{doc.Minor}, schema hash {hash}. Unknown fields must be ignored by readers. Enums are the member name on CBOR and in JSON written as a string or, without a converter, as the number.")).Append(",\n");
        sb.Append("  \"x-protocol\": { \"major\": ").Append(doc.Major.ToString(CultureInfo.InvariantCulture)).Append(", \"minor\": ").Append(doc.Minor.ToString(CultureInfo.InvariantCulture)).Append(" },\n");
        sb.Append("  \"x-schemaHash\": ").Append(Quote(hash)).Append(",\n");
        sb.Append("  \"x-messages\": {\n");
        for (var i = 0; i < doc.Messages.Count; i++)
        {
            sb.Append("    ").Append(Quote(doc.Messages[i].Type)).Append(": { \"$ref\": ").Append(Quote("#/$defs/" + doc.Messages[i].Payload)).Append(" }").Append(i + 1 < doc.Messages.Count ? ",\n" : "\n");
        }

        sb.Append("  },\n  \"$defs\": {\n");
        for (var i = 0; i < doc.Types.Count; i++)
        {
            var t = doc.Types[i];
            sb.Append("    ").Append(Quote(t.Name)).Append(": ");
            if (t.IsEnum)
            {
                sb.Append("{ \"anyOf\": [ { \"type\": \"string\", \"enum\": [")
                    .Append(string.Join(", ", t.Members.Select(m => Quote(m.Name))))
                    .Append("] }, { \"type\": \"integer\", \"enum\": [")
                    .Append(string.Join(", ", t.Members.Select(m => m.Value.ToString(CultureInfo.InvariantCulture))))
                    .Append("] } ] }");
            }
            else
            {
                sb.Append("{\n      \"type\": \"object\",\n      \"properties\": {\n");
                for (var j = 0; j < t.Fields.Count; j++)
                {
                    var f = t.Fields[j];
                    var inner = JsonType(f.Type);
                    sb.Append("        ").Append(Quote(f.Name)).Append(": ").Append(f.Nullable ? "{ \"anyOf\": [ " + inner + ", { \"type\": \"null\" } ] }" : inner).Append(j + 1 < t.Fields.Count ? ",\n" : "\n");
                }

                var required = t.Fields.Where(f => !f.Nullable && !f.HasDefault).Select(f => Quote(f.Name)).ToList();
                sb.Append("      },\n      \"required\": [").Append(string.Join(", ", required)).Append("],\n      \"additionalProperties\": true\n    }");
            }

            sb.Append(i + 1 < doc.Types.Count ? ",\n" : "\n");
        }

        sb.Append("  }\n}\n");
        return sb.ToString();
    }

    private static string JsonType(string type)
    {
        var core = Core(type, out var nullable);
        var text = JsonCore(core);
        return nullable ? "{ \"anyOf\": [ " + text + ", { \"type\": \"null\" } ] }" : text;
    }

    private static string JsonCore(string core)
    {
        switch (core)
        {
            case "string": return "{ \"type\": \"string\" }";
            case "bool": return "{ \"type\": \"boolean\" }";
            case "uint8": return "{ \"type\": \"integer\", \"minimum\": 0, \"maximum\": 255 }";
            case "int32": return "{ \"type\": \"integer\", \"minimum\": -2147483648, \"maximum\": 2147483647 }";
            case "int64": return "{ \"type\": \"integer\" }";
            case "float32":
            case "float64":
            case "decimal": return "{ \"type\": \"number\" }";
            case "guid": return "{ \"type\": \"string\", \"format\": \"uuid\", \"pattern\": " + Quote(UuidPattern) + " }";
            case "datetime": return "{ \"type\": \"string\", \"format\": \"date-time\" }";
            case "float32[]": return "{ \"type\": \"array\", \"items\": { \"type\": \"number\" } }";
        }

        if (IsList(core, out var element)) return "{ \"type\": \"array\", \"items\": " + JsonType(element) + " }";
        if (core.StartsWith("ref:", StringComparison.Ordinal)) return "{ \"$ref\": " + Quote("#/$defs/" + core.Substring(4)) + " }";
        return "{}";
    }

    // ---- CDDL --------------------------------------------------------------------------------------------------

    public static string Cddl(SchemaDoc doc, string hash)
    {
        var names = RuleNames(doc);
        var sb = new StringBuilder();
        sb.Append("; Bazaar wire payloads for the CBOR codec, protocol ").Append(doc.Major.ToString(CultureInfo.InvariantCulture)).Append('.').Append(doc.Minor.ToString(CultureInfo.InvariantCulture)).Append(" (RFC 8610).\n");
        sb.Append("; Schema hash ").Append(hash).Append(".\n");
        sb.Append("; Map keys are text. Absent keys mean null or the default. Readers ignore unknown keys. Generated; do not edit.\n\n");
        sb.Append("envelope = {\n  v: uint,\n  t: tstr,\n  k: \"req\" / \"res\" / \"evt\" / \"err\",\n  ? id: tstr,\n  ? re: tstr,\n  ? s: tstr,\n  ? n: int,\n  ? ts: #6.0(tstr),\n  ? p: any,\n  * tstr => any\n}\n\n");
        sb.Append("uuid-text = tstr .regexp ").Append(Quote(UuidPattern.Substring(1, UuidPattern.Length - 2))).Append('\n');
        sb.Append("decimal-fraction = #6.4([exponent: int, mantissa: int])\n");
        sb.Append("float32-array = #6.85(bstr)\n\n");

        sb.Append("; Message types and the payload rule each carries.\n");
        foreach (var m in doc.Messages) sb.Append("; ").Append(m.Type).Append(" = ").Append(names[m.Payload]).Append('\n');

        var roots = doc.Types.Where(t => t.Payload).Select(t => names[t.Name]).ToList();
        sb.Append("\npayload = ").Append(roots.Count == 0 ? "any" : string.Join(" / ", roots)).Append("\n\n");

        foreach (var t in doc.Types)
        {
            sb.Append(names[t.Name]).Append(" = ");
            if (t.IsEnum)
            {
                sb.Append(t.Members.Count == 0 ? "tstr" : string.Join(" / ", t.Members.Select(m => Quote(m.Name)))).Append('\n');
                continue;
            }

            sb.Append("{\n");
            foreach (var f in t.Fields)
            {
                var optional = f.Nullable || f.HasDefault;
                sb.Append("  ").Append(optional ? "? " : "").Append(f.Name).Append(": ").Append(CddlType(f.Type, names)).Append(",  ; ").Append(f.Position.ToString(CultureInfo.InvariantCulture)).Append('\n');
            }

            sb.Append("  * tstr => any\n}\n");
        }

        return sb.ToString();
    }

    private static string CddlType(string type, Dictionary<string, string> names)
    {
        var core = Core(type, out var nullable);
        string text;
        switch (core)
        {
            case "string": text = "tstr"; break;
            case "bool": text = "bool"; break;
            case "uint8": text = "0..255"; break;
            case "int32": text = "-2147483648..2147483647"; break;
            case "int64": text = "int"; break;
            case "float32": text = "float32"; break;
            case "float64": text = "float64"; break;
            case "decimal": text = "decimal-fraction"; break;
            case "guid": text = "uuid-text"; break;
            case "datetime": text = "#6.0(tstr)"; break;
            case "float32[]": text = "float32-array"; break;
            default:
                if (IsList(core, out var element)) text = "[* " + CddlType(element, names) + "]";
                else if (core.StartsWith("ref:", StringComparison.Ordinal)) text = names[core.Substring(4)];
                else text = "any";
                break;
        }

        return nullable ? "(" + text + " / null)" : text;
    }

    private static Dictionary<string, string> RuleNames(SchemaDoc doc)
    {
        var shortNames = doc.Types.ToDictionary(t => t.Name, t => Kebab(t.Name.Substring(t.Name.LastIndexOf('.') + 1)), StringComparer.Ordinal);
        var counts = shortNames.Values.GroupBy(v => v, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.Count(), StringComparer.Ordinal);
        var reserved = new HashSet<string>(StringComparer.Ordinal) { "envelope", "payload", "uuid-text", "decimal-fraction", "float32-array" };
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var t in doc.Types)
        {
            var name = shortNames[t.Name];
            result[t.Name] = counts[name] > 1 || reserved.Contains(name) ? Kebab(t.Name.Replace('.', '-')) : name;
        }

        return result;
    }

    private static string Kebab(string name)
    {
        var sb = new StringBuilder();
        for (var i = 0; i < name.Length; i++)
        {
            var c = name[i];
            if (c == '.') { sb.Append('-'); continue; }
            if (char.IsUpper(c) && i > 0 && sb.Length > 0 && sb[sb.Length - 1] != '-' && (char.IsLower(name[i - 1]) || char.IsDigit(name[i - 1]) || (i + 1 < name.Length && char.IsLower(name[i + 1])))) sb.Append('-');
            sb.Append(char.ToLowerInvariant(c));
        }

        return sb.ToString();
    }
}
