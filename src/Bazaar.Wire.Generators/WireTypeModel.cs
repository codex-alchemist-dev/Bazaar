using System.Globalization;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace Bazaar.Wire.Generators;

/// <summary>Maps a C# type onto a <see cref="WireKind"/>, reporting a diagnostic for anything the wire cannot carry. Shared by every codec emitter.</summary>
internal sealed class TypeResolver
{
    private readonly List<DiagnosticInfo> _diagnostics;

    public TypeResolver(List<DiagnosticInfo> diagnostics)
    {
        _diagnostics = diagnostics;
    }

    public TypeRef? Resolve(ITypeSymbol symbol, NullableAnnotation annotation)
    {
        var nullable = false;
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
            case SpecialType.System_String: return new TypeRef(symbol, nullable, WireKind.String, null);
            case SpecialType.System_Boolean: return new TypeRef(symbol, nullable, WireKind.Bool, null);
            case SpecialType.System_Byte: return new TypeRef(symbol, nullable, WireKind.Byte, null);
            case SpecialType.System_Int32: return new TypeRef(symbol, nullable, WireKind.Int32, null);
            case SpecialType.System_Int64: return new TypeRef(symbol, nullable, WireKind.Int64, null);
            case SpecialType.System_Single: return new TypeRef(symbol, nullable, WireKind.Single, null);
            case SpecialType.System_Double: return new TypeRef(symbol, nullable, WireKind.Double, null);
            case SpecialType.System_Decimal: return new TypeRef(symbol, nullable, WireKind.Decimal, null);
            case SpecialType.System_DateTime: return Unsupported(symbol);
        }

        if (symbol.TypeKind == TypeKind.Enum) return new TypeRef(symbol, nullable, WireKind.Enum, null);

        if (symbol is IArrayTypeSymbol array)
        {
            if (array.Rank != 1) return Unsupported(symbol);
            if (array.ElementType.SpecialType == SpecialType.System_Single) return new TypeRef(symbol, nullable, WireKind.Float32Array, null);
            var element = Resolve(array.ElementType, array.ElementNullableAnnotation);
            return element is null ? null : new TypeRef(symbol, nullable, WireKind.Array, element);
        }

        if (symbol is INamedTypeSymbol named)
        {
            var fq = named.OriginalDefinition.ToDisplayString();
            switch (fq)
            {
                case "System.Guid": return new TypeRef(symbol, nullable, WireKind.Guid, null);
                case "System.DateTimeOffset": return new TypeRef(symbol, nullable, WireKind.DateTimeOffset, null);
                case "System.ReadOnlyMemory<T>" when named.TypeArguments[0].SpecialType == SpecialType.System_Single:
                    return new TypeRef(symbol, nullable, WireKind.Float32Memory, null);
                case "System.Collections.Generic.IReadOnlyList<T>":
                case "System.Collections.Generic.List<T>":
                    {
                        var element = Resolve(named.TypeArguments[0], named.TypeArgumentNullableAnnotations[0]);
                        if (element is null) return null;
                        return new TypeRef(symbol, nullable, fq.StartsWith("System.Collections.Generic.List", StringComparison.Ordinal) ? WireKind.List : WireKind.ReadOnlyList, element);
                    }
            }

            if (named.TypeParameters.Length == 0 && named.TypeKind is TypeKind.Class or TypeKind.Struct && named.DeclaredAccessibility == Accessibility.Public)
            {
                return new TypeRef(symbol, nullable, WireKind.Record, null);
            }
        }

        return Unsupported(symbol);
    }

    private TypeRef? Unsupported(ITypeSymbol symbol)
    {
        _diagnostics.Add(new DiagnosticInfo(Descriptors.UnsupportedType, symbol.ToDisplayString()));
        return null;
    }
}

/// <summary>One wire field of a payload record: the property written, the constructor parameter or setter that reads it back.</summary>
internal sealed class WireField
{
    public string Key = "";
    public TypeRef WriteType = null!;
    public TypeRef ReadType = null!;
    public IParameterSymbol? Parameter;
    public IPropertySymbol Property = null!;
    public bool Settable;
    public string Local = "";
}

/// <summary>The wire fields of a payload record and the constructor the reader calls. Shared by every codec emitter.</summary>
internal sealed class RecordShape
{
    private RecordShape(List<WireField> fields, IMethodSymbol constructor)
    {
        Fields = fields;
        Constructor = constructor;
    }

    public List<WireField> Fields { get; }

    public IMethodSymbol Constructor { get; }

    /// <summary>Returns null (after reporting a diagnostic) when the record cannot be generated for.</summary>
    public static RecordShape? Analyze(INamedTypeSymbol symbol, TypeResolver resolver, List<DiagnosticInfo> diagnostics)
    {
        var fields = new List<WireField>();
        var keys = new HashSet<string>(StringComparer.Ordinal);
        foreach (var property in symbol.GetMembers().OfType<IPropertySymbol>())
        {
            if (property.IsStatic || property.IsIndexer || property.Name == "EqualityContract") continue;
            if (property.DeclaredAccessibility != Accessibility.Public || property.GetMethod is not { DeclaredAccessibility: Accessibility.Public }) continue;
            if (property.GetAttributes().Any(a => a.AttributeClass?.Name == "JsonIgnoreAttribute")) continue;

            var writeType = resolver.Resolve(property.Type, property.NullableAnnotation);
            if (writeType is null) continue;
            var key = CborEmitter.Camel(property.Name);
            if (!keys.Add(key)) diagnostics.Add(new DiagnosticInfo(Descriptors.DuplicateKey, symbol.ToDisplayString()));
            fields.Add(new WireField
            {
                Key = key,
                WriteType = writeType,
                ReadType = writeType,
                Property = property,
                Settable = property.SetMethod is { DeclaredAccessibility: Accessibility.Public },
                Local = "f" + fields.Count.ToString(CultureInfo.InvariantCulture),
            });
        }

        IMethodSymbol? constructor = symbol.InstanceConstructors
            .Where(c => c.DeclaredAccessibility == Accessibility.Public
                && !(c.Parameters.Length == 1 && SymbolEqualityComparer.Default.Equals(c.Parameters[0].Type, symbol)))
            .OrderByDescending(c => c.Parameters.Length)
            .FirstOrDefault();
        if (constructor is null)
        {
            diagnostics.Add(new DiagnosticInfo(Descriptors.NoConstructor, symbol.ToDisplayString()));
            return null;
        }

        foreach (var parameter in constructor.Parameters)
        {
            var field = fields.FirstOrDefault(f => string.Equals(f.Property.Name, parameter.Name, StringComparison.OrdinalIgnoreCase));
            if (field is null)
            {
                diagnostics.Add(new DiagnosticInfo(Descriptors.NoConstructor, symbol.ToDisplayString() + " (parameter " + parameter.Name + ")"));
                return null;
            }

            var readType = resolver.Resolve(parameter.Type, parameter.NullableAnnotation);
            if (readType is null) return null;
            field.Parameter = parameter;
            field.ReadType = readType;
        }

        return new RecordShape(fields, constructor);
    }

    public static string InitialValue(WireField field)
    {
        if (field.Parameter is { HasExplicitDefaultValue: true } p)
        {
            var value = p.ExplicitDefaultValue;
            if (value is null) return "default";
            if (field.ReadType.Kind == WireKind.Enum) return $"({field.ReadType.NonNull.Display()}){Convert.ToString(value, CultureInfo.InvariantCulture)}";
            return SymbolDisplay.FormatPrimitive(value, true, false);
        }

        return field.ReadType.Nullable || field.ReadType.Symbol.IsValueType ? "default" : "default!";
    }

    /// <summary>The constructor-call text and object-initialiser tail that rebuild the record from the read locals.</summary>
    public string Construct(string display, List<WireField> readable)
    {
        var args = string.Join(", ", Constructor.Parameters.Select(p => readable.First(f => f.Parameter is not null && SymbolEqualityComparer.Default.Equals(f.Parameter, p)).Local));
        var initializers = readable.Where(f => f.Parameter is null).ToList();
        var tail = initializers.Count == 0 ? "" : " { " + string.Join(", ", initializers.Select(f => $"{f.Property.Name} = {f.Local}")) + " }";
        return $"new {display}({args}){tail}";
    }
}
