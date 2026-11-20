using Microsoft.CodeAnalysis;

namespace Bazaar.Wire.Generators;

/// <summary>Maps a contracts type to the wire shapes every generated codec understands (doc 90 section 3). Shared by the CBOR and Protobuf emitters.</summary>
internal static class TypeResolver
{
    public static TypeRef? Resolve(ITypeSymbol symbol, NullableAnnotation annotation, List<DiagnosticInfo> diagnostics)
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
            case SpecialType.System_DateTime: return Unsupported(symbol, diagnostics);
        }

        if (symbol.TypeKind == TypeKind.Enum) return new TypeRef(symbol, nullable, WireKind.Enum, null);

        if (symbol is IArrayTypeSymbol array)
        {
            if (array.Rank != 1) return Unsupported(symbol, diagnostics);
            if (array.ElementType.SpecialType == SpecialType.System_Single) return new TypeRef(symbol, nullable, WireKind.Float32Array, null);
            var element = Resolve(array.ElementType, array.ElementNullableAnnotation, diagnostics);
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
                        var element = Resolve(named.TypeArguments[0], named.TypeArgumentNullableAnnotations[0], diagnostics);
                        if (element is null) return null;
                        return new TypeRef(symbol, nullable, fq.StartsWith("System.Collections.Generic.List", StringComparison.Ordinal) ? WireKind.List : WireKind.ReadOnlyList, element);
                    }
            }

            if (named.TypeParameters.Length == 0 && named.TypeKind is TypeKind.Class or TypeKind.Struct && named.DeclaredAccessibility == Accessibility.Public)
            {
                return new TypeRef(symbol, nullable, WireKind.Record, null);
            }
        }

        return Unsupported(symbol, diagnostics);
    }

    private static TypeRef? Unsupported(ITypeSymbol symbol, List<DiagnosticInfo> diagnostics)
    {
        diagnostics.Add(new DiagnosticInfo(Descriptors.UnsupportedType, symbol.ToDisplayString()));
        return null;
    }
}
