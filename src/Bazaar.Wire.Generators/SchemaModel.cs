using System.Globalization;
using System.Text;
using Microsoft.CodeAnalysis;

namespace Bazaar.Wire.Generators;

/// <summary>One field of a payload record. <see cref="Position"/> is its 1-based field number: constructor order, append-only.</summary>
internal sealed class SchemaField
{
    public int Position;
    public string Name = "";

    /// <summary>Canonical type text: a primitive name, <c>float32[]</c>, <c>list&lt;T&gt;</c> or <c>ref:Full.Type.Name</c>; a trailing <c>?</c> only on list elements.</summary>
    public string Type = "";

    public bool Nullable;
    public bool HasDefault;
}

internal sealed class SchemaEnumMember
{
    public string Name = "";
    public long Value;
}

internal sealed class SchemaType
{
    public string Name = "";
    public bool IsEnum;
    public bool Payload;
    public List<SchemaField> Fields = new();
    public List<SchemaEnumMember> Members = new();
    public string? Fallback;
}

internal sealed class SchemaMessage
{
    public string Type = "";
    public string Payload = "";
}

/// <summary>The published schema (doc 90 section 5): protocol version, message types and the field layout of every payload type.</summary>
internal sealed class SchemaDoc
{
    public int Major;
    public int Minor;
    public string Hash = "";
    public List<SchemaMessage> Messages = new();
    public List<SchemaType> Types = new();
}

/// <summary>Builds a <see cref="SchemaDoc"/> from the <c>[WirePayload]</c> records, following property types the way the codec generators do.</summary>
internal static class SchemaBuilder
{
    public static SchemaDoc Build(Compilation compilation, List<PayloadInfo> payloads, List<DiagnosticInfo>? diagnostics, System.Threading.CancellationToken ct)
    {
        var doc = new SchemaDoc { Major = 1, Minor = 0 };
        var protocol = compilation.GetTypeByMetadataName("Bazaar.Contracts.Wire.WireProtocol");
        if (protocol is not null)
        {
            foreach (var member in protocol.GetMembers().OfType<IFieldSymbol>())
            {
                if (!member.IsConst || member.ConstantValue is not int value) continue;
                if (member.Name == "Major") doc.Major = value;
                else if (member.Name == "Minor") doc.Minor = value;
            }
        }

        var byName = new Dictionary<string, SchemaType>(StringComparer.Ordinal);
        var queue = new Queue<INamedTypeSymbol>();
        var context = new Context(diagnostics, byName, queue);

        foreach (var payload in payloads)
        {
            var name = payload.Type.ToDisplayString();
            context.Enqueue(payload.Type);
            foreach (var messageType in payload.MessageTypes) doc.Messages.Add(new SchemaMessage { Type = messageType, Payload = name });
        }

        foreach (var payload in payloads) context.MarkPayload(payload.Type.ToDisplayString());

        while (queue.Count > 0)
        {
            ct.ThrowIfCancellationRequested();
            var symbol = queue.Dequeue();
            var type = byName[symbol.ToDisplayString()];
            if (symbol.TypeKind == TypeKind.Enum) FillEnum(symbol, type);
            else FillRecord(symbol, type, context);
        }

        doc.Messages.Sort((a, b) => string.CompareOrdinal(a.Type, b.Type));
        doc.Types.AddRange(byName.Values.OrderBy(t => t.Name, StringComparer.Ordinal));
        foreach (var name in context.PayloadNames)
        {
            if (byName.TryGetValue(name, out var t)) t.Payload = true;
        }

        return doc;
    }

    private sealed class Context
    {
        private readonly List<DiagnosticInfo>? _diagnostics;
        private readonly Dictionary<string, SchemaType> _byName;
        private readonly Queue<INamedTypeSymbol> _queue;

        public Context(List<DiagnosticInfo>? diagnostics, Dictionary<string, SchemaType> byName, Queue<INamedTypeSymbol> queue)
        {
            _diagnostics = diagnostics;
            _byName = byName;
            _queue = queue;
        }

        public HashSet<string> PayloadNames { get; } = new(StringComparer.Ordinal);

        public void MarkPayload(string name) => PayloadNames.Add(name);

        public void Enqueue(INamedTypeSymbol symbol)
        {
            var name = symbol.ToDisplayString();
            if (_byName.ContainsKey(name)) return;
            _byName[name] = new SchemaType { Name = name, IsEnum = symbol.TypeKind == TypeKind.Enum };
            _queue.Enqueue(symbol);
        }

        public bool Resolve(ITypeSymbol symbol, NullableAnnotation annotation, out string type, out bool nullable)
        {
            type = "";
            nullable = false;
            if (symbol is INamedTypeSymbol { IsValueType: true, OriginalDefinition.SpecialType: SpecialType.System_Nullable_T } wrapper)
            {
                symbol = wrapper.TypeArguments[0];
                nullable = true;
            }
            else if (symbol.IsReferenceType && annotation == NullableAnnotation.Annotated)
            {
                nullable = true;
            }

            switch (symbol.SpecialType)
            {
                case SpecialType.System_String: type = "string"; return true;
                case SpecialType.System_Boolean: type = "bool"; return true;
                case SpecialType.System_Byte: type = "uint8"; return true;
                case SpecialType.System_Int32: type = "int32"; return true;
                case SpecialType.System_Int64: type = "int64"; return true;
                case SpecialType.System_Single: type = "float32"; return true;
                case SpecialType.System_Double: type = "float64"; return true;
                case SpecialType.System_Decimal: type = "decimal"; return true;
            }

            if (symbol.TypeKind == TypeKind.Enum)
            {
                Enqueue((INamedTypeSymbol)symbol);
                type = "ref:" + symbol.ToDisplayString();
                return true;
            }

            if (symbol is IArrayTypeSymbol array)
            {
                if (array.Rank != 1) return Unsupported(symbol);
                if (array.ElementType.SpecialType == SpecialType.System_Single)
                {
                    type = "float32[]";
                    return true;
                }

                return List(array.ElementType, array.ElementNullableAnnotation, out type);
            }

            if (symbol is INamedTypeSymbol named)
            {
                var fq = named.OriginalDefinition.ToDisplayString();
                switch (fq)
                {
                    case "System.Guid": type = "guid"; return true;
                    case "System.DateTimeOffset": type = "datetime"; return true;
                    case "System.ReadOnlyMemory<T>" when named.TypeArguments[0].SpecialType == SpecialType.System_Single:
                        type = "float32[]";
                        return true;
                    case "System.Collections.Generic.IReadOnlyList<T>":
                    case "System.Collections.Generic.List<T>":
                        return List(named.TypeArguments[0], named.TypeArgumentNullableAnnotations[0], out type);
                }

                if (named.TypeParameters.Length == 0 && named.TypeKind is TypeKind.Class or TypeKind.Struct && named.DeclaredAccessibility == Accessibility.Public)
                {
                    Enqueue(named);
                    type = "ref:" + named.ToDisplayString();
                    return true;
                }
            }

            return Unsupported(symbol);
        }

        private bool List(ITypeSymbol element, NullableAnnotation annotation, out string type)
        {
            type = "";
            if (!Resolve(element, annotation, out var inner, out var innerNullable)) return false;
            type = "list<" + inner + (innerNullable ? "?" : "") + ">";
            return true;
        }

        private bool Unsupported(ITypeSymbol symbol)
        {
            _diagnostics?.Add(new DiagnosticInfo(Descriptors.UnsupportedType, symbol.ToDisplayString()));
            return false;
        }
    }

    private static void FillEnum(INamedTypeSymbol symbol, SchemaType type)
    {
        foreach (var field in symbol.GetMembers().OfType<IFieldSymbol>())
        {
            if (!field.IsConst) continue;
            var wire = CborEmitter.Camel(field.Name);
            foreach (var attribute in field.GetAttributes())
            {
                if (attribute.AttributeClass?.Name == "JsonStringEnumMemberNameAttribute"
                    && attribute.ConstructorArguments.Length == 1
                    && attribute.ConstructorArguments[0].Value is string custom)
                {
                    wire = custom;
                }
            }

            type.Members.Add(new SchemaEnumMember { Name = wire, Value = Convert.ToInt64(field.ConstantValue, CultureInfo.InvariantCulture) });
            if (field.Name == "Unknown" || (field.Name == "Unspecified" && type.Fallback is null)) type.Fallback = wire;
        }
    }

    private static void FillRecord(INamedTypeSymbol symbol, SchemaType type, Context context)
    {
        var properties = new List<IPropertySymbol>();
        foreach (var property in symbol.GetMembers().OfType<IPropertySymbol>())
        {
            if (property.IsStatic || property.IsIndexer || property.Name == "EqualityContract") continue;
            if (property.DeclaredAccessibility != Accessibility.Public || property.GetMethod is not { DeclaredAccessibility: Accessibility.Public }) continue;
            if (property.GetAttributes().Any(a => a.AttributeClass?.Name == "JsonIgnoreAttribute")) continue;
            properties.Add(property);
        }

        var constructor = symbol.InstanceConstructors
            .Where(c => c.DeclaredAccessibility == Accessibility.Public
                && !(c.Parameters.Length == 1 && SymbolEqualityComparer.Default.Equals(c.Parameters[0].Type, symbol)))
            .OrderByDescending(c => c.Parameters.Length)
            .FirstOrDefault();

        var ordered = new List<(IPropertySymbol Property, IParameterSymbol? Parameter)>();
        if (constructor is not null)
        {
            foreach (var parameter in constructor.Parameters)
            {
                var property = properties.FirstOrDefault(p => string.Equals(p.Name, parameter.Name, StringComparison.OrdinalIgnoreCase));
                if (property is not null) ordered.Add((property, parameter));
            }
        }

        foreach (var property in properties)
        {
            if (ordered.Any(o => SymbolEqualityComparer.Default.Equals(o.Property, property))) continue;
            if (property.SetMethod is { DeclaredAccessibility: Accessibility.Public }) ordered.Add((property, null));
        }

        foreach (var (property, parameter) in ordered)
        {
            if (!context.Resolve(property.Type, property.NullableAnnotation, out var fieldType, out var nullable)) continue;
            type.Fields.Add(new SchemaField
            {
                Position = type.Fields.Count + 1,
                Name = CborEmitter.Camel(property.Name),
                Type = fieldType,
                Nullable = nullable,
                HasDefault = parameter is { HasExplicitDefaultValue: true },
            });
        }
    }
}

/// <summary>One difference between the checked-in lock and the current schema.</summary>
internal sealed class SchemaChange
{
    public SchemaChange(bool breaking, string message)
    {
        Breaking = breaking;
        Message = message;
    }

    public bool Breaking { get; }

    public string Message { get; }
}

/// <summary>Compares the current schema with the checked-in lock (doc 90 section 3 point 2).</summary>
internal static class SchemaDiff
{
    public static List<SchemaChange> Compare(SchemaDoc locked, SchemaDoc current)
    {
        var changes = new List<SchemaChange>();
        if (current.Major < locked.Major)
        {
            changes.Add(new SchemaChange(true, $"protocol major went backwards ({locked.Major} to {current.Major})"));
        }

        var currentMessages = current.Messages.ToDictionary(m => m.Type, StringComparer.Ordinal);
        foreach (var message in locked.Messages)
        {
            if (!currentMessages.TryGetValue(message.Type, out var now)) changes.Add(new SchemaChange(true, $"message type '{message.Type}' was removed"));
            else if (now.Payload != message.Payload) changes.Add(new SchemaChange(true, $"message type '{message.Type}' now carries {now.Payload}, was {message.Payload}"));
        }

        var lockedMessages = new HashSet<string>(locked.Messages.Select(m => m.Type), StringComparer.Ordinal);
        foreach (var message in current.Messages.Where(m => !lockedMessages.Contains(m.Type))) changes.Add(new SchemaChange(false, $"message type '{message.Type}' was added"));

        var currentTypes = current.Types.ToDictionary(t => t.Name, StringComparer.Ordinal);
        foreach (var old in locked.Types)
        {
            if (!currentTypes.TryGetValue(old.Name, out var now))
            {
                changes.Add(new SchemaChange(true, $"type {old.Name} was removed"));
                continue;
            }

            if (old.IsEnum != now.IsEnum)
            {
                changes.Add(new SchemaChange(true, $"type {old.Name} changed between record and enum"));
            }
            else if (old.IsEnum)
            {
                CompareEnum(old, now, changes);
            }
            else
            {
                CompareRecord(old, now, changes);
            }
        }

        var lockedTypes = new HashSet<string>(locked.Types.Select(t => t.Name), StringComparer.Ordinal);
        foreach (var type in current.Types.Where(t => !lockedTypes.Contains(t.Name))) changes.Add(new SchemaChange(false, $"type {type.Name} was added"));

        if (current.Major > locked.Major || current.Minor != locked.Minor)
        {
            changes.Add(new SchemaChange(false, $"protocol version is {current.Major}.{current.Minor}, lock has {locked.Major}.{locked.Minor}"));
        }

        if (current.Major > locked.Major)
        {
            // A major bump is the sanctioned way to break compatibility: the changes still need a new lock, but none of them is an error.
            return changes.Select(c => c.Breaking ? new SchemaChange(false, c.Message + " (allowed by the protocol major bump)") : c).ToList();
        }

        return changes;
    }

    private static void CompareEnum(SchemaType old, SchemaType now, List<SchemaChange> changes)
    {
        foreach (var member in old.Members)
        {
            var match = now.Members.FirstOrDefault(m => m.Name == member.Name);
            if (match is null) changes.Add(new SchemaChange(true, $"enum {old.Name}: member '{member.Name}' was removed or renamed"));
            else if (match.Value != member.Value) changes.Add(new SchemaChange(true, $"enum {old.Name}: member '{member.Name}' changed value from {member.Value} to {match.Value}"));
        }

        foreach (var member in now.Members.Where(m => old.Members.All(o => o.Name != m.Name))) changes.Add(new SchemaChange(false, $"enum {old.Name}: member '{member.Name}' was added"));
        if (old.Fallback is not null && now.Fallback is null) changes.Add(new SchemaChange(true, $"enum {old.Name}: fallback member '{old.Fallback}' was removed, so an unknown value would now throw"));
    }

    private static void CompareRecord(SchemaType old, SchemaType now, List<SchemaChange> changes)
    {
        foreach (var field in old.Fields)
        {
            var at = now.Fields.FirstOrDefault(f => f.Position == field.Position);
            if (at is null)
            {
                changes.Add(new SchemaChange(true, $"{old.Name}: field {field.Position} '{field.Name}' was removed"));
                continue;
            }

            if (at.Name != field.Name)
            {
                var moved = now.Fields.Any(f => f.Name == field.Name);
                changes.Add(new SchemaChange(true, moved
                    ? $"{old.Name}: field '{field.Name}' was reordered (was position {field.Position}, position {field.Position} is now '{at.Name}')"
                    : $"{old.Name}: field {field.Position} was renamed from '{field.Name}' to '{at.Name}'"));
                continue;
            }

            if (at.Type != field.Type || at.Nullable != field.Nullable)
            {
                changes.Add(new SchemaChange(true, $"{old.Name}.{field.Name}: type changed from {Describe(field)} to {Describe(at)}"));
            }
            else if (field.HasDefault && !at.HasDefault && !at.Nullable)
            {
                changes.Add(new SchemaChange(true, $"{old.Name}.{field.Name}: the default value was removed, so an older sender that omits it would now fail"));
            }
        }

        foreach (var field in now.Fields.Where(f => f.Position > old.Fields.Count))
        {
            if (!field.Nullable && !field.HasDefault) changes.Add(new SchemaChange(true, $"{old.Name}.{field.Name}: new field {field.Position} is not nullable and has no default, so an older sender cannot produce it"));
            else changes.Add(new SchemaChange(false, $"{old.Name}.{field.Name}: new field {field.Position} was added"));
        }
    }

    private static string Describe(SchemaField f) => f.Type + (f.Nullable ? "?" : "");
}

/// <summary>A minimal JSON reader for <c>schema.lock.json</c>. The analyzer assembly carries no System.Text.Json.</summary>
internal static class MiniJson
{
    public static object? Parse(string text)
    {
        var position = 0;
        var value = ReadValue(text, ref position);
        SkipSpace(text, ref position);
        if (position != text.Length) throw new FormatException("trailing data");
        return value;
    }

    private static void SkipSpace(string s, ref int i)
    {
        while (i < s.Length && char.IsWhiteSpace(s[i])) i++;
    }

    private static object? ReadValue(string s, ref int i)
    {
        SkipSpace(s, ref i);
        if (i >= s.Length) throw new FormatException("unexpected end");
        switch (s[i])
        {
            case '{':
                {
                    i++;
                    var map = new Dictionary<string, object?>(StringComparer.Ordinal);
                    SkipSpace(s, ref i);
                    if (s[i] == '}') { i++; return map; }
                    while (true)
                    {
                        SkipSpace(s, ref i);
                        var key = ReadString(s, ref i);
                        SkipSpace(s, ref i);
                        if (s[i++] != ':') throw new FormatException("expected ':'");
                        map[key] = ReadValue(s, ref i);
                        SkipSpace(s, ref i);
                        if (s[i] == ',') { i++; continue; }
                        if (s[i] == '}') { i++; return map; }
                        throw new FormatException("expected ',' or '}'");
                    }
                }

            case '[':
                {
                    i++;
                    var list = new List<object?>();
                    SkipSpace(s, ref i);
                    if (s[i] == ']') { i++; return list; }
                    while (true)
                    {
                        list.Add(ReadValue(s, ref i));
                        SkipSpace(s, ref i);
                        if (s[i] == ',') { i++; continue; }
                        if (s[i] == ']') { i++; return list; }
                        throw new FormatException("expected ',' or ']'");
                    }
                }

            case '"': return ReadString(s, ref i);
            case 't': Expect(s, ref i, "true"); return true;
            case 'f': Expect(s, ref i, "false"); return false;
            case 'n': Expect(s, ref i, "null"); return null;
            default:
                {
                    var start = i;
                    while (i < s.Length && (char.IsDigit(s[i]) || s[i] is '-' or '+' or '.' or 'e' or 'E')) i++;
                    if (!long.TryParse(s.Substring(start, i - start), NumberStyles.Integer, CultureInfo.InvariantCulture, out var number)) throw new FormatException("bad number");
                    return number;
                }
        }
    }

    private static void Expect(string s, ref int i, string word)
    {
        if (string.CompareOrdinal(s, i, word, 0, word.Length) != 0) throw new FormatException("bad literal");
        i += word.Length;
    }

    private static string ReadString(string s, ref int i)
    {
        if (s[i] != '"') throw new FormatException("expected string");
        i++;
        var sb = new StringBuilder();
        while (s[i] != '"')
        {
            var c = s[i++];
            if (c != '\\') { sb.Append(c); continue; }
            var e = s[i++];
            switch (e)
            {
                case 'n': sb.Append('\n'); break;
                case 'r': sb.Append('\r'); break;
                case 't': sb.Append('\t'); break;
                case 'b': sb.Append('\b'); break;
                case 'f': sb.Append('\f'); break;
                case 'u':
                    sb.Append((char)int.Parse(s.Substring(i, 4), NumberStyles.HexNumber, CultureInfo.InvariantCulture));
                    i += 4;
                    break;
                default: sb.Append(e); break;
            }
        }

        i++;
        return sb.ToString();
    }

    private static Dictionary<string, object?> Obj(object? o) => o as Dictionary<string, object?> ?? throw new InvalidCastException();

    private static List<object?> Arr(object? o) => o as List<object?> ?? throw new InvalidCastException();

    private static string Str(object? o) => o as string ?? throw new InvalidCastException();

    private static long Num(object? o) => o is long n ? n : throw new InvalidCastException();

    private static bool Flag(object? o) => o is bool b ? b : throw new InvalidCastException();

    private static object? Get(Dictionary<string, object?> map, string key) => map.TryGetValue(key, out var value) ? value : throw new KeyNotFoundException(key);

    public static SchemaDoc? ToDoc(string text)
    {
        try
        {
            var root = Obj(Parse(text));
            var doc = new SchemaDoc();
            var protocol = Obj(Get(root, "protocol"));
            doc.Major = (int)Num(Get(protocol, "major"));
            doc.Minor = (int)Num(Get(protocol, "minor"));
            doc.Hash = root.TryGetValue("hash", out var hash) && hash is string h ? h : "";
            foreach (var item in Arr(Get(root, "messages")))
            {
                var m = Obj(item);
                doc.Messages.Add(new SchemaMessage { Type = Str(Get(m, "type")), Payload = Str(Get(m, "payload")) });
            }

            foreach (var item in Arr(Get(root, "types")))
            {
                var t = Obj(item);
                var type = new SchemaType { Name = Str(Get(t, "name")), IsEnum = Str(Get(t, "kind")) == "enum", Payload = t.TryGetValue("payload", out var p) && p is true };
                if (type.IsEnum)
                {
                    type.Fallback = t.TryGetValue("fallback", out var fb) ? fb as string : null;
                    foreach (var member in Arr(Get(t, "members")))
                    {
                        var m = Obj(member);
                        type.Members.Add(new SchemaEnumMember { Name = Str(Get(m, "name")), Value = Num(Get(m, "value")) });
                    }
                }
                else
                {
                    foreach (var field in Arr(Get(t, "fields")))
                    {
                        var f = Obj(field);
                        type.Fields.Add(new SchemaField
                        {
                            Position = (int)Num(Get(f, "n")),
                            Name = Str(Get(f, "name")),
                            Type = Str(Get(f, "type")),
                            Nullable = Flag(Get(f, "nullable")),
                            HasDefault = Flag(Get(f, "default")),
                        });
                    }
                }

                doc.Types.Add(type);
            }

            return doc;
        }
        catch (Exception e) when (e is FormatException or InvalidCastException or KeyNotFoundException or IndexOutOfRangeException or ArgumentOutOfRangeException or OverflowException)
        {
            return null;
        }
    }
}
