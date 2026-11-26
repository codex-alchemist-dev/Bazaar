using System.Globalization;
using System.Text;
using Microsoft.CodeAnalysis;

namespace Bazaar.Wire.Generators;

/// <summary>
/// Emits the Protobuf side of the wire contract (doc 90 sections 3 and 5): the <c>wire.proto</c> text, with field numbers taken
/// from declaration order (1-based, append-only), and the C# mapping layer between the <c>[WirePayload]</c> records and the
/// messages <c>protoc</c> generates from that file (the S6 mapping layer, generated instead of hand-written). The mapper
/// enforces every decode limit, and it never uses <c>Any</c> or extensions.
/// </summary>
internal static class ProtobufEmitter
{
    internal const string ProtoNamespace = "Bazaar.Wire.Codec.Protobuf.Proto";

    private const string _p = "global::" + ProtoNamespace;
    private const string _rt = "global::Bazaar.Wire.Codec.Protobuf.ProtobufWireRuntime";
    private const string _lim = "global::Bazaar.Wire.Codec.Protobuf.ProtobufLimits";
    private const string _repeated = "global::Google.Protobuf.Collections.RepeatedField";

    /// <summary>Protobuf names that the C# generator reserves inside a message; a wire field may not use one.</summary>
    private static readonly HashSet<string> _reservedMembers = new(StringComparer.Ordinal)
    {
        "Descriptor", "Parser", "Clone", "Equals", "GetHashCode", "ToString", "WriteTo", "MergeFrom", "CalculateSize", "Types", "ClearField", "Class",
    };

    public static (string Proto, string Source) Emit(Compilation compilation, List<PayloadInfo> payloads, List<DiagnosticInfo> diagnostics, System.Threading.CancellationToken ct)
    {
        var model = new Model(diagnostics, ct);
        foreach (var payload in payloads) model.AddRecord(payload.Type);
        model.Drain();
        return (BuildProto(model), BuildSource(compilation, payloads, model));
    }

    private static bool IsList(WireKind kind) =>
        kind is WireKind.ReadOnlyList or WireKind.List or WireKind.Array or WireKind.Float32Array or WireKind.Float32Memory;

    private static bool IsMessage(WireKind kind) => kind is WireKind.Record or WireKind.DateTimeOffset;

    // ---------------------------------------------------------------------------------------------------------------- model

    private sealed class Field
    {
        public IPropertySymbol Property = null!;
        public TypeRef Type = null!;
        public int Number;
        public string ProtoName = "";
        public IParameterSymbol? Parameter;
        public bool Settable;
        public string Local = "";
    }

    private sealed class Message
    {
        public INamedTypeSymbol Symbol = null!;
        public string Name = "";
        public List<Field> Fields = new();
        public IMethodSymbol Constructor = null!;
    }

    private sealed class EnumInfo
    {
        public INamedTypeSymbol Symbol = null!;
        public string Name = "";
        public List<(string Field, string Wire, long Value)> Members = new();
        public string? Fallback;
    }

    private sealed class Model
    {
        private readonly List<DiagnosticInfo> _diagnostics;
        private readonly System.Threading.CancellationToken _ct;
        private readonly TypeResolver _resolver;
        private readonly Queue<INamedTypeSymbol> _queue = new();
        private readonly HashSet<string> _seen = new(StringComparer.Ordinal);

        public Model(List<DiagnosticInfo> diagnostics, System.Threading.CancellationToken ct)
        {
            _diagnostics = diagnostics;
            _ct = ct;
            _resolver = new TypeResolver(diagnostics);
        }

        public SortedDictionary<string, Message> Messages { get; } = new(StringComparer.Ordinal);

        public SortedDictionary<string, EnumInfo> Enums { get; } = new(StringComparer.Ordinal);

        /// <summary>Wrapper messages for nullable lists, by name, with the proto element type.</summary>
        public SortedDictionary<string, string> Wrappers { get; } = new(StringComparer.Ordinal);

        public void AddRecord(INamedTypeSymbol symbol)
        {
            if (_seen.Add(CborEmitter.Fq(symbol))) _queue.Enqueue(symbol);
        }

        public void Drain()
        {
            while (_queue.Count > 0)
            {
                _ct.ThrowIfCancellationRequested();
                Build(_queue.Dequeue());
            }
        }

        private void Clash(string name)
        {
            _diagnostics.Add(new DiagnosticInfo(Descriptors.ProtobufNameClash, name));
        }

        private void Build(INamedTypeSymbol symbol)
        {
            var message = new Message { Symbol = symbol, Name = symbol.Name };
            if (message.Name is "Envelope" or "Instant" || Messages.ContainsKey(message.Name) || Enums.ContainsKey(message.Name))
            {
                Clash(message.Name);
                return;
            }

            var number = 1;
            foreach (var property in symbol.GetMembers().OfType<IPropertySymbol>())
            {
                if (property.IsStatic || property.IsIndexer || property.Name == "EqualityContract") continue;
                if (property.DeclaredAccessibility != Accessibility.Public || property.GetMethod is not { DeclaredAccessibility: Accessibility.Public }) continue;
                if (property.GetAttributes().Any(a => a.AttributeClass?.Name == "JsonIgnoreAttribute")) continue;

                var type = _resolver.Resolve(property.Type, property.NullableAnnotation);
                if (type is null) continue;
                if (!Accept(property, type, symbol)) continue;

                if (_reservedMembers.Contains(property.Name) || property.Name == symbol.Name || property.Name.EndsWith("FieldNumber", StringComparison.Ordinal))
                {
                    _diagnostics.Add(new DiagnosticInfo(Descriptors.ProtobufReservedName, symbol.ToDisplayString() + "." + property.Name));
                    continue;
                }

                message.Fields.Add(new Field
                {
                    Property = property,
                    Type = type,
                    Number = number++,
                    ProtoName = Snake(property.Name),
                    Settable = property.SetMethod is { DeclaredAccessibility: Accessibility.Public },
                    Local = "f" + message.Fields.Count.ToString(CultureInfo.InvariantCulture),
                });
            }

            var constructor = symbol.InstanceConstructors
                .Where(c => c.DeclaredAccessibility == Accessibility.Public
                    && !(c.Parameters.Length == 1 && SymbolEqualityComparer.Default.Equals(c.Parameters[0].Type, symbol)))
                .OrderByDescending(c => c.Parameters.Length)
                .FirstOrDefault();
            if (constructor is null)
            {
                _diagnostics.Add(new DiagnosticInfo(Descriptors.NoConstructor, symbol.ToDisplayString()));
                return;
            }

            foreach (var parameter in constructor.Parameters)
            {
                var field = message.Fields.FirstOrDefault(f => string.Equals(f.Property.Name, parameter.Name, StringComparison.OrdinalIgnoreCase));
                if (field is null)
                {
                    _diagnostics.Add(new DiagnosticInfo(Descriptors.NoConstructor, symbol.ToDisplayString() + " (parameter " + parameter.Name + ")"));
                    return;
                }

                field.Parameter = parameter;
            }

            message.Constructor = constructor;
            Messages[message.Name] = message;
        }

        /// <summary>Checks the shapes Protobuf cannot express, and queues the records and enums a field refers to.</summary>
        private bool Accept(IPropertySymbol property, TypeRef type, INamedTypeSymbol owner)
        {
            var where = owner.ToDisplayString() + "." + property.Name;
            if (IsList(type.Kind))
            {
                if (type.Element is { } element)
                {
                    if (element.Nullable || IsList(element.Kind))
                    {
                        _diagnostics.Add(new DiagnosticInfo(Descriptors.ProtobufUnsupportedShape, where));
                        return false;
                    }

                    Register(element);
                    var proto = ProtoType(element);
                    if (type.Nullable) Wrappers[WrapperName(proto)] = proto;
                }
                else if (type.Nullable)
                {
                    Wrappers["FloatList"] = "float";
                }

                return true;
            }

            Register(type);
            return true;
        }

        private void Register(TypeRef type)
        {
            if (type.Kind == WireKind.Record)
            {
                AddRecord((INamedTypeSymbol)type.Symbol);
            }
            else if (type.Kind == WireKind.Enum)
            {
                var symbol = (INamedTypeSymbol)type.Symbol;
                if (Enums.ContainsKey(symbol.Name)) return;
                if (Messages.ContainsKey(symbol.Name) || symbol.Name is "Envelope" or "Instant")
                {
                    Clash(symbol.Name);
                    return;
                }

                var info = new EnumInfo { Symbol = symbol, Name = symbol.Name };
                var values = new HashSet<long>();
                foreach (var field in symbol.GetMembers().OfType<IFieldSymbol>())
                {
                    if (!field.IsConst || field.ConstantValue is null) continue;
                    var value = Convert.ToInt64(field.ConstantValue, CultureInfo.InvariantCulture);
                    if (value is < int.MinValue or > int.MaxValue || !values.Add(value)) continue;
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

                    info.Members.Add((field.Name, wire, value));
                    if (field.Name == "Unknown" || (field.Name == "Unspecified" && info.Fallback is null)) info.Fallback = field.Name;
                }

                Enums[info.Name] = info;
            }
        }

    }

    internal static string ProtoType(TypeRef type) => type.Kind switch
    {
        WireKind.String => "string",
        WireKind.Bool => "bool",
        WireKind.Byte => "uint32",
        WireKind.Int32 => "int32",
        WireKind.Int64 => "int64",
        WireKind.Single or WireKind.Float32Array or WireKind.Float32Memory => "float",
        WireKind.Double => "double",
        WireKind.Decimal => "string",
        WireKind.Guid => "bytes",
        WireKind.DateTimeOffset => "Instant",
        WireKind.Enum => type.Symbol.Name,
        _ => type.Symbol.Name,
    };

    private static string WrapperName(string protoType) => protoType switch
    {
        "string" => "StringList",
        "bytes" => "BytesList",
        "bool" => "BoolList",
        "float" => "FloatList",
        "double" => "DoubleList",
        "int32" => "Int32List",
        "int64" => "Int64List",
        "uint32" => "UInt32List",
        _ => protoType + "List",
    };

    /// <summary>PascalCase to snake_case, one underscore before every capital, so protoc's PascalCase gives the same name back.</summary>
    internal static string Snake(string name)
    {
        var sb = new StringBuilder();
        for (var i = 0; i < name.Length; i++)
        {
            var c = name[i];
            if (char.IsUpper(c))
            {
                if (i > 0) sb.Append('_');
                sb.Append(char.ToLowerInvariant(c));
            }
            else
            {
                sb.Append(c);
            }
        }

        return sb.ToString();
    }

    private static string ScreamingSnake(string name) => Snake(name).ToUpperInvariant();

    // ------------------------------------------------------------------------------------------------------------ wire.proto

    private static string BuildProto(Model model)
    {
        var sb = new StringBuilder();
        sb.Append("""
            // Bazaar wire schema, codec "protobuf" (docs/design/90-wire-transports-and-codecs.md).
            // GENERATED by Bazaar.Wire.Generators from the [WirePayload] records in Bazaar.Contracts. Do not edit by hand:
            // run `BAZAAR_UPDATE_WIRE_PROTO=1 dotnet test tests/Bazaar.Wire.Generators.Tests` to refresh it. A test fails when it drifts.
            //
            // Rules (append-only):
            //  * A field number is the 1-based position of the property in the record. New properties go last and must be
            //    nullable or have a default. Never reorder, remove or retype a published field.
            //  * Every payload is one message. A frame is an Envelope whose `p` holds one payload message, chosen by `t`.
            //  * Unknown fields are ignored. No Any, no extensions, no groups.
            //  * Absent optional fields are null; absent scalars read as their proto3 default.
            //  * Guid is 16 bytes in RFC 4122 order (big-endian fields). Decimal is its invariant-culture text. Byte is a
            //    uint32 that must fit in 8 bits. DateTimeOffset is an Instant. Enums are the numeric values of the C# enum.
            //  * A nullable list is wrapped in a *List message so that "no list" differs from "empty list".
            //  * An empty payload message would be zero bytes, which Envelope.p cannot tell from "no payload". The codec
            //    therefore writes the two-byte payload F8 FF FF FF 0F 00 (field 536870911, varint 0) for it. Readers ignore it
            //    like any unknown field.
            //  * Envelope.end is always true and last. A frame without it is truncated and is rejected.
            syntax = "proto3";

            package bazaar.wire.v1;

            option csharp_namespace = "Bazaar.Wire.Codec.Protobuf.Proto";

            message Envelope {
              int32 v = 1;
              string t = 2;
              string k = 3;
              optional string id = 4;
              optional string re = 5;
              optional string s = 6;
              optional int64 n = 7;
              Instant ts = 8;
              bytes p = 9;
              bool end = 10;
            }

            message Instant {
              int64 utc_ticks = 1;
              sint32 offset_minutes = 2;
            }

            """);
        sb.AppendLine();

        foreach (var wrapper in model.Wrappers)
        {
            sb.Append("message ").Append(wrapper.Key).AppendLine(" {");
            sb.Append("  repeated ").Append(wrapper.Value).AppendLine(" items = 1;");
            sb.AppendLine("}");
            sb.AppendLine();
        }

        foreach (var info in model.Enums.Values)
        {
            var prefix = ScreamingSnake(info.Name);
            sb.Append("enum ").Append(info.Name).AppendLine(" {");
            var members = info.Members.OrderBy(m => m.Value == 0 ? 0 : 1).ThenBy(m => m.Value).ToList();
            if (members.Count == 0 || members[0].Value != 0) sb.Append("  ").Append(prefix).AppendLine("_NONE = 0;");
            foreach (var (field, wire, value) in members)
            {
                sb.Append("  ").Append(prefix).Append('_').Append(ScreamingSnake(field)).Append(" = ").Append(value.ToString(CultureInfo.InvariantCulture))
                    .Append("; // ").AppendLine(wire);
            }

            sb.AppendLine("}");
            sb.AppendLine();
        }

        foreach (var message in model.Messages.Values)
        {
            sb.Append("message ").Append(message.Name).AppendLine(" {");
            foreach (var f in message.Fields)
            {
                sb.Append("  ").Append(FieldDeclaration(model, f)).Append(' ').Append(f.ProtoName).Append(" = ")
                    .Append(f.Number.ToString(CultureInfo.InvariantCulture)).AppendLine(";");
            }

            sb.AppendLine("}");
            sb.AppendLine();
        }

        return sb.ToString().Replace("\r\n", "\n").TrimEnd('\n') + "\n";
    }

    private static string FieldDeclaration(Model model, Field f)
    {
        var t = f.Type;
        if (IsList(t.Kind))
        {
            var element = t.Element is null ? "float" : ProtoType(t.Element);
            return t.Nullable ? WrapperName(element) : "repeated " + element;
        }

        var proto = ProtoType(t);
        return t.Nullable && !IsMessage(t.Kind) ? "optional " + proto : proto;
    }

    // -------------------------------------------------------------------------------------------------------------- mapper

    private static string BuildSource(Compilation compilation, List<PayloadInfo> payloads, Model model)
    {
        var emitter = new MapperWriter();
        var ns = compilation.AssemblyName is { Length: > 0 } name ? name + ".Generated" : "Generated";
        var sb = new StringBuilder();
        sb.AppendLine("// <auto-generated/>");
        sb.AppendLine("// Generated by Bazaar.Wire.Generators from the [WirePayload] records in Bazaar.Contracts. Do not edit.");
        sb.AppendLine("#nullable enable");
        sb.AppendLine("#pragma warning disable");
        sb.Append("namespace ").Append(ns).AppendLine(";");
        sb.AppendLine();
        sb.AppendLine("/// <summary>The closed set of wire payload types and their Protobuf mappers.</summary>");
        sb.AppendLine("internal static partial class WireProtobufPayloads");
        sb.AppendLine("{");

        sb.AppendLine("    /// <summary>Every registered payload type with the message types it travels under (empty for replies and shared shapes).</summary>");
        sb.AppendLine("    public static global::System.Collections.Generic.IReadOnlyList<(global::System.Type Type, global::System.Collections.Generic.IReadOnlyList<string> MessageTypes)> All { get; } =");
        sb.AppendLine("    [");
        foreach (var payload in payloads)
        {
            var types = string.Join(", ", payload.MessageTypes.Select(t => "\"" + CborEmitter.Escape(t) + "\""));
            sb.Append("        (typeof(").Append(CborEmitter.Fq(payload.Type)).Append("), new string[] { ").Append(types).AppendLine(" }),");
        }

        sb.AppendLine("    ];");
        sb.AppendLine();
        sb.AppendLine("    /// <summary>True when <typeparamref name=\"T\"/> is a registered wire payload.</summary>");
        sb.AppendLine("    public static bool IsRegistered<T>()");
        sb.AppendLine("    {");
        sb.Append("        return ");
        sb.Append(payloads.Count == 0 ? "false" : string.Join("\n            || ", payloads.Select(p => $"typeof(T) == typeof({CborEmitter.Fq(p.Type)})")));
        sb.AppendLine(";");
        sb.AppendLine("    }");
        sb.AppendLine();

        sb.AppendLine("    /// <summary>Encodes one payload as one Protobuf message.</summary>");
        sb.AppendLine("    public static byte[] Write<T>(T value)");
        sb.AppendLine("    {");
        foreach (var payload in payloads)
        {
            var fq = CborEmitter.Fq(payload.Type);
            sb.AppendLine($"        if (typeof(T) == typeof({fq})) return global::Google.Protobuf.MessageExtensions.ToByteArray(To_{payload.Type.Name}(({fq})(object)value!));");
        }

        sb.AppendLine("        throw new global::System.NotSupportedException(typeof(T).Name + \" is not a registered wire payload.\");");
        sb.AppendLine("    }");
        sb.AppendLine();

        sb.AppendLine("    /// <summary>Decodes one payload, enforcing the decode limits. The payload record is nesting level 2, below the envelope.</summary>");
        sb.AppendLine($"    public static T Read<T>(global::System.ReadOnlyMemory<byte> data, {_lim} l)");
        sb.AppendLine("    {");
        foreach (var payload in payloads)
        {
            var fq = CborEmitter.Fq(payload.Type);
            sb.AppendLine($"        if (typeof(T) == typeof({fq})) return (T)(object)From_{payload.Type.Name}({_p}.{payload.Type.Name}.Parser.ParseFrom({_rt}.NewInput(data, l)), l, 2);");
        }

        sb.AppendLine("        throw new global::System.NotSupportedException(typeof(T).Name + \" is not a registered wire payload.\");");
        sb.AppendLine("    }");
        sb.AppendLine();

        foreach (var info in model.Enums.Values) MapperWriter.Enum(sb, info);
        foreach (var message in model.Messages.Values) emitter.Record(sb, message);
        sb.Append(emitter.Lists);
        sb.AppendLine("}");
        sb.AppendLine();

        sb.AppendLine("/// <summary>The generated <c>wire.proto</c> text, for the drift test and for publishing the schema.</summary>");
        sb.AppendLine("internal static class WireProtoSchema");
        sb.AppendLine("{");
        sb.AppendLine("    public const string Text = \"" + BuildProto(model).Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\n", "\\n") + "\";");
        sb.AppendLine("}");
        return sb.ToString();
    }

    private sealed class MapperWriter
    {
        private readonly Dictionary<string, int> _listIds = new(StringComparer.Ordinal);

        public StringBuilder Lists { get; } = new();

        private static string Fq(TypeRef type) => type.NonNull.Display();

        /// <summary>The C# type protoc generates for one proto element of this wire type.</summary>
        private static string CsProto(TypeRef type) => type.Kind switch
        {
            WireKind.String or WireKind.Decimal => "string",
            WireKind.Bool => "bool",
            WireKind.Byte => "uint",
            WireKind.Int32 => "int",
            WireKind.Int64 => "long",
            WireKind.Single or WireKind.Float32Array or WireKind.Float32Memory => "float",
            WireKind.Double => "double",
            WireKind.Guid => "global::Google.Protobuf.ByteString",
            WireKind.DateTimeOffset => _p + ".Instant",
            _ => _p + "." + type.Symbol.Name,
        };

        private static string ToProto(TypeRef type, string x) => type.Kind switch
        {
            WireKind.Byte => $"(uint){x}",
            WireKind.Decimal => $"{_rt}.DecimalText({x})",
            WireKind.Guid => $"{_rt}.GuidBytes({x})",
            WireKind.DateTimeOffset => $"{_rt}.ToInstant({x})",
            WireKind.Enum => $"ToProto_{type.Symbol.Name}({x})",
            WireKind.Record => $"To_{type.Symbol.Name}({x})",
            _ => x,
        };

        private static string FromProto(TypeRef type, string x, string depth, string key) => type.Kind switch
        {
            WireKind.String => $"{_rt}.Text({x}, l)",
            WireKind.Byte => $"{_rt}.Byte({x})",
            WireKind.Decimal => $"{_rt}.Decimal({x}, l)",
            WireKind.Guid => $"{_rt}.Guid({x})",
            WireKind.DateTimeOffset => $"{_rt}.FromInstant({x})",
            WireKind.Enum => $"FromProto_{type.Symbol.Name}({x})",
            WireKind.Record => $"From_{type.Symbol.Name}({x}, l, {depth})",
            _ => x,
        };

        public static void Enum(StringBuilder sb, EnumInfo info)
        {
            var fq = CborEmitter.Fq(info.Symbol);
            sb.AppendLine($"    private static {_p}.{info.Name} ToProto_{info.Name}({fq} v) => v switch");
            sb.AppendLine("    {");
            foreach (var (field, _, value) in info.Members) sb.AppendLine($"        {fq}.{field} => ({_p}.{info.Name}){value.ToString(CultureInfo.InvariantCulture)},");
            sb.AppendLine("        _ => throw new global::System.ArgumentOutOfRangeException(nameof(v)),");
            sb.AppendLine("    };");
            sb.AppendLine($"    private static {fq} FromProto_{info.Name}({_p}.{info.Name} p) => (int)p switch");
            sb.AppendLine("    {");
            foreach (var (field, _, value) in info.Members) sb.AppendLine($"        {value.ToString(CultureInfo.InvariantCulture)} => {fq}.{field},");
            sb.AppendLine(info.Fallback is null
                ? $"        var n => throw {_rt}.UnknownEnum(\"{info.Name}\", n),"
                : $"        _ => {fq}.{info.Fallback},");
            sb.AppendLine("    };");
            sb.AppendLine();
        }

        public void Record(StringBuilder sb, Message message)
        {
            var fq = CborEmitter.Fq(message.Symbol);
            var name = message.Name;

            sb.AppendLine($"    private static {_p}.{name} To_{name}({fq} v)");
            sb.AppendLine("    {");
            sb.AppendLine($"        var m = new {_p}.{name}();");
            foreach (var f in message.Fields) sb.AppendLine("        " + WriteField(f));
            sb.AppendLine("        return m;");
            sb.AppendLine("    }");

            sb.AppendLine($"    private static {fq} From_{name}({_p}.{name} m, {_lim} l, int d)");
            sb.AppendLine("    {");
            sb.AppendLine($"        {_rt}.CheckDepth(d, l);");
            var readable = message.Fields.Where(f => f.Parameter is not null || f.Settable).ToList();
            foreach (var f in readable) sb.AppendLine($"        var {f.Local} = {ReadField(f)};");
            var args = string.Join(", ", message.Constructor.Parameters.Select(p => readable.First(f => f.Parameter is not null && SymbolEqualityComparer.Default.Equals(f.Parameter, p)).Local));
            var initializers = readable.Where(f => f.Parameter is null).ToList();
            var tail = initializers.Count == 0 ? "" : " { " + string.Join(", ", initializers.Select(f => $"{f.Property.Name} = {f.Local}")) + " }";
            sb.AppendLine($"        return new {fq}({args}){tail};");
            sb.AppendLine("    }");
            sb.AppendLine();
        }

        private string WriteField(Field f)
        {
            var t = f.Type;
            var member = "m." + f.Property.Name;
            var value = "v." + f.Property.Name;
            if (IsList(t.Kind))
            {
                var id = ListId(t.NonNull);
                if (!t.Nullable) return $"WL{id}({member}, {value});";
                return $"if ({value} is {{ }} x{f.Local}) {{ var w{f.Local} = new {_p}.{WrapperName(ProtoOf(t))}(); WL{id}(w{f.Local}.Items, x{f.Local}); {member} = w{f.Local}; }}";
            }

            return t.Nullable
                ? $"if ({value} is {{ }} x{f.Local}) {member} = {ToProto(t.NonNull, "x" + f.Local)};"
                : $"{member} = {ToProto(t, value)};";
        }

        private string ReadField(Field f)
        {
            var t = f.Type;
            var member = "m." + f.Property.Name;
            var display = t.Display();
            if (IsList(t.Kind))
            {
                var id = ListId(t.NonNull);
                return t.Nullable
                    ? $"{member} is {{ }} w{f.Local} ? ({display})RL{id}(w{f.Local}.Items, l, d + 1) : default"
                    : $"RL{id}({member}, l, d + 1)";
            }

            var key = CborEmitter.Escape(f.ProtoName);
            if (IsMessage(t.Kind))
            {
                return t.Nullable
                    ? $"{member} is {{ }} x{f.Local} ? ({display}){FromProto(t.NonNull, "x" + f.Local, "d + 1", key)} : default"
                    : FromProto(t, $"{_rt}.Require({member}, \"{key}\")", "d + 1", key);
            }

            return t.Nullable
                ? $"{member.Replace("m.", "m.Has")} ? ({display}){FromProto(t.NonNull, member, "d + 1", key)} : default"
                : FromProto(t, member, "d + 1", key);
        }

        private static string ProtoOf(TypeRef list) => list.Element is null ? "float" : ProtoType(list.Element);

        /// <summary>Emits (once) the writer and reader of one list type and returns its id.</summary>
        private int ListId(TypeRef list)
        {
            var key = list.Display();
            if (_listIds.TryGetValue(key, out var id)) return id;
            id = _listIds.Count;
            _listIds[key] = id;

            var display = list.Display();
            var element = list.Element;
            var protoCs = element is null ? "float" : CsProto(element);
            var elementDisplay = element is null ? "float" : Fq(element);

            Lists.AppendLine($"    // {display}");
            Lists.AppendLine($"    private static void WL{id}({_repeated}<{protoCs}> r, {display} v)");
            Lists.AppendLine("    {");
            switch (list.Kind)
            {
                case WireKind.Float32Array:
                    Lists.AppendLine("        r.AddRange(v);");
                    break;
                case WireKind.Float32Memory:
                    Lists.AppendLine("        foreach (var e in v.Span) r.Add(e);");
                    break;
                default:
                    {
                        var count = list.Kind == WireKind.Array ? "Length" : "Count";
                        Lists.AppendLine($"        for (var i = 0; i < v.{count}; i++) r.Add({ToProto(element!, "v[i]")});");
                        break;
                    }
            }

            Lists.AppendLine("    }");

            Lists.AppendLine($"    private static {display} RL{id}({_repeated}<{protoCs}> r, {_lim} l, int d)");
            Lists.AppendLine("    {");
            Lists.AppendLine($"        {_rt}.CheckList(r.Count, l, d);");
            switch (list.Kind)
            {
                case WireKind.Float32Array:
                    Lists.AppendLine("        return global::System.Linq.Enumerable.ToArray(r);");
                    break;
                case WireKind.Float32Memory:
                    Lists.AppendLine("        return new global::System.ReadOnlyMemory<float>(global::System.Linq.Enumerable.ToArray(r));");
                    break;
                default:
                    Lists.AppendLine($"        var list = new global::System.Collections.Generic.List<{elementDisplay}>(r.Count);");
                    Lists.AppendLine($"        foreach (var e in r) list.Add({FromProto(element!, "e", "d + 1", "items")});");
                    Lists.AppendLine(list.Kind == WireKind.Array ? "        return list.ToArray();" : "        return list;");
                    break;
            }

            Lists.AppendLine("    }");
            Lists.AppendLine();
            return id;
        }
    }
}
